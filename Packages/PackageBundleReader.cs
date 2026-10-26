using System.IO.Compression;

namespace BcDeployment;

public sealed class PackageBundleReader(AdminCenterOptions options)
{
    private const long MaximumAppBytes = 50L * 1024 * 1024;
    private const long MaximumZipBytes = 250L * 1024 * 1024;
    private const long MaximumExpandedBytes = 500L * 1024 * 1024;
    private const int MaximumArchiveEntries = 100;
    private const double MaximumCompressionRatio = 100;

    public PreparedPackageSet Prepare(string packagePath)
    {
        var input = ValidateInput(packagePath);
        var extension = input.Extension.ToLowerInvariant();
        if (extension == ".app")
        {
            var package = NavxPackageReader.Read(input.FullName, input.Name, MaximumAppBytes);
            var ordered = OrderPackages([package], out var externalDependencies);
            return new PreparedPackageSet(ordered, externalDependencies, null);
        }

        if (extension != ".zip")
        {
            throw new ArgumentException("Package path must reference an .app or .zip file.", nameof(packagePath));
        }

        return PrepareZip(input);
    }

    private PreparedPackageSet PrepareZip(FileInfo zipFile)
    {
        if (zipFile.Length is <= 0 or > MaximumZipBytes)
        {
            throw new ArgumentException("The ZIP file must be between 1 byte and 250 MB.", nameof(zipFile));
        }

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "BcDeployment", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            using var archive = ZipFile.OpenRead(zipFile.FullName);
            if (archive.Entries.Count > MaximumArchiveEntries)
            {
                throw new InvalidDataException($"The ZIP file contains more than {MaximumArchiveEntries} entries.");
            }

            var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var packages = new List<AppPackage>();
            long expandedBytes = 0;

            foreach (var entry in archive.Entries)
            {
                ValidateArchiveEntry(entry, entryNames);
                expandedBytes += entry.Length;
                if (expandedBytes > MaximumExpandedBytes)
                {
                    throw new InvalidDataException("The ZIP file expands beyond the 500 MB limit.");
                }

                if (!entry.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entry.Length is <= 0 or > MaximumAppBytes)
                {
                    throw new InvalidDataException($"App '{entry.FullName}' must be between 1 byte and 50 MB.");
                }

                var extractedPath = Path.Combine(temporaryDirectory, $"{packages.Count:D3}.app");
                using (var source = entry.Open())
                using (var destination = new FileStream(
                    extractedPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    source.CopyTo(destination);
                }

                packages.Add(NavxPackageReader.Read(
                    extractedPath,
                    Path.GetFileName(entry.FullName),
                    MaximumAppBytes));
            }

            if (packages.Count == 0)
            {
                throw new InvalidDataException("The ZIP file contains no .app packages.");
            }

            var ordered = OrderPackages(packages, out var externalDependencies);
            return new PreparedPackageSet(ordered, externalDependencies, temporaryDirectory);
        }
        catch
        {
            Directory.Delete(temporaryDirectory, true);
            throw;
        }
    }

    private static void ValidateArchiveEntry(ZipArchiveEntry entry, HashSet<string> entryNames)
    {
        var normalizedName = entry.FullName.Replace('\\', '/');
        if (normalizedName.StartsWith('/')
            || Path.IsPathRooted(entry.FullName)
            || normalizedName.Split('/').Contains("..", StringComparer.Ordinal))
        {
            throw new InvalidDataException($"ZIP entry '{entry.FullName}' has an unsafe path.");
        }

        if (!entryNames.Add(normalizedName))
        {
            throw new InvalidDataException($"ZIP entry '{entry.FullName}' is duplicated.");
        }

        var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixFileType == 0xA000)
        {
            throw new InvalidDataException($"ZIP entry '{entry.FullName}' is a symbolic link.");
        }

        if (entry.Length > 0
            && (entry.CompressedLength == 0 || (double)entry.Length / entry.CompressedLength > MaximumCompressionRatio))
        {
            throw new InvalidDataException($"ZIP entry '{entry.FullName}' exceeds the compression-ratio limit.");
        }
    }

    private static IReadOnlyList<AppPackage> OrderPackages(
        IReadOnlyList<AppPackage> packages,
        out IReadOnlyList<AppDependency> externalDependencies)
    {
        var duplicate = packages.GroupBy(package => package.AppId).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Multiple packages use app ID {duplicate.Key:D}.");
        }

        var byId = packages.ToDictionary(package => package.AppId);
        var state = new Dictionary<Guid, VisitState>();
        var ordered = new List<AppPackage>(packages.Count);
        var external = new Dictionary<Guid, AppDependency>();
        var path = new Stack<AppPackage>();

        foreach (var package in packages)
        {
            Visit(package);
        }

        externalDependencies = external.Values.OrderBy(dependency => dependency.Name, StringComparer.Ordinal).ToArray();
        return ordered;

        void Visit(AppPackage package)
        {
            if (state.TryGetValue(package.AppId, out var currentState))
            {
                if (currentState == VisitState.Visiting)
                {
                    var cycle = string.Join(" -> ", path.Reverse().Select(item => item.Name).Append(package.Name));
                    throw new InvalidDataException($"App dependency cycle detected: {cycle}.");
                }

                return;
            }

            state[package.AppId] = VisitState.Visiting;
            path.Push(package);
            foreach (var dependency in package.Dependencies)
            {
                if (!byId.TryGetValue(dependency.AppId, out var dependencyPackage))
                {
                    external.TryAdd(dependency.AppId, dependency);
                    continue;
                }

                if (dependencyPackage.Version < dependency.MinimumVersion)
                {
                    throw new InvalidDataException(
                        $"App '{package.Name}' requires '{dependencyPackage.Name}' {dependency.MinimumVersion} or later, but the bundle contains {dependencyPackage.Version}.");
                }

                Visit(dependencyPackage);
            }

            path.Pop();
            state[package.AppId] = VisitState.Visited;
            ordered.Add(package);
        }
    }

    private FileInfo ValidateInput(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new ArgumentException("Package path is required.", nameof(packagePath));
        }

        var fullPath = Path.GetFullPath(packagePath);
        if (!options.AllowedAppRoots.Any(root => IsWithinRoot(fullPath, root)))
        {
            throw new UnauthorizedAccessException("The package path is outside BC_ALLOWED_APP_ROOTS.");
        }

        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The package file was not found.");
        }

        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("Package paths cannot be symbolic links or reparse points.");
        }

        return file;
    }

    private static bool IsWithinRoot(string fullPath, string root)
    {
        var relativePath = Path.GetRelativePath(root, fullPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return !Path.IsPathRooted(relativePath)
            && !relativePath.Equals("..", comparison)
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", comparison);
    }

    private enum VisitState
    {
        Visiting,
        Visited
    }
}
