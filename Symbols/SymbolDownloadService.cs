using System.Security.Cryptography;

namespace BcDeployment;

public sealed class SymbolDownloadService(
    BusinessCentralDevClient devClient,
    AdminCenterOptions options)
{
    private const long MaximumSymbolBytes = 150L * 1024 * 1024;

    public async Task<SymbolDownloadBatchResult> DownloadManyAsync(
        IReadOnlyList<Guid> appIds,
        string environmentName,
        string symbolDirectory,
        bool force,
        int maxParallelDownloads,
        CancellationToken cancellationToken)
    {
        var requestedAppIds = ValidateAppIds(appIds);
        if (string.IsNullOrWhiteSpace(environmentName))
        {
            throw new ArgumentException("Environment name is required.", nameof(environmentName));
        }

        if (maxParallelDownloads is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxParallelDownloads),
            "Parallel download limit must be between 1 and 4.");
        }

        var destinationDirectory = ResolveDestinationDirectory(symbolDirectory);
        Directory.CreateDirectory(destinationDirectory);
        RejectReparsePoints(destinationDirectory);

        var results = new SymbolDownloadItemResult[requestedAppIds.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, requestedAppIds.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(maxParallelDownloads, requestedAppIds.Count),
                CancellationToken = cancellationToken
            },
            async (index, token) =>
            {
                var appId = requestedAppIds[index];
                try
                {
                    var symbol = await DownloadAsync(
                        appId,
                        environmentName,
                        destinationDirectory,
                        force,
                        token);
                    results[index] = new SymbolDownloadItemResult(
                        appId,
                        symbol.Status,
                        symbol,
                        null);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    results[index] = new SymbolDownloadItemResult(
                        appId,
                        "failed",
                        null,
                        SafeMessage(exception));
                }
            });

        var status = results.All(result => result.Status == "failed")
            ? "failed"
            : results.Any(result => result.Status == "failed")
                ? "partial"
                : "succeeded";
        return new SymbolDownloadBatchResult(status, environmentName, results);
    }

    public async Task<SymbolDownloadResult> DownloadAsync(
        Guid appId,
        string environmentName,
        string symbolDirectory,
        bool force,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = ResolveDestinationDirectory(symbolDirectory);
        Directory.CreateDirectory(destinationDirectory);
        RejectReparsePoints(destinationDirectory);

        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".bc-symbol-{Guid.NewGuid():N}.tmp");
        try
        {
            await devClient.DownloadPackageAsync(
                appId,
                environmentName,
                temporaryPath,
                MaximumSymbolBytes,
                cancellationToken);
            var package = NavxPackageReader.Read(
                temporaryPath,
                "downloaded symbol package",
                MaximumSymbolBytes);
            if (package.AppId != appId)
            {
                throw new InvalidDataException(
                    $"The development endpoint returned app ID {package.AppId:D}, but {appId:D} was requested.");
            }

            var fileName = $"{SanitizeFileNamePart(package.Publisher)}_{SanitizeFileNamePart(package.Name)}_{package.Version}.app";
            var finalPath = Path.Combine(destinationDirectory, fileName);
            var downloadedHash = await ComputeSha256Async(temporaryPath, cancellationToken);
            string status;
            if (File.Exists(finalPath))
            {
                RejectReparsePoint(finalPath);
                var existingHash = await ComputeSha256Async(finalPath, cancellationToken);
                if (existingHash.Equals(downloadedHash, StringComparison.OrdinalIgnoreCase))
                {
                    status = "alreadyPresent";
                }
                else if (!force)
                {
                    throw new IOException(
                        $"A different package already exists at '{finalPath}'. Set force to true to replace it.");
                }
                else
                {
                    File.Move(temporaryPath, finalPath, overwrite: true);
                    status = "replaced";
                }
            }
            else
            {
                File.Move(temporaryPath, finalPath);
                status = "downloaded";
            }

            return new SymbolDownloadResult(
                status,
                package.AppId,
                package.Name,
                package.Publisher,
                package.Version.ToString(),
                environmentName,
                finalPath,
                new FileInfo(finalPath).Length,
                downloadedHash,
                package.Dependencies.Select(dependency => new DependencySummary(
                    dependency.AppId,
                    dependency.Name,
                    dependency.Publisher,
                    dependency.MinimumVersion.ToString())).ToArray());
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string ResolveDestinationDirectory(string symbolDirectory)
    {
        if (string.IsNullOrWhiteSpace(symbolDirectory))
        {
            throw new ArgumentException("A symbol directory is required.", nameof(symbolDirectory));
        }

        if (options.AllowedSymbolRoots.Count == 0)
        {
            throw new InvalidOperationException("BC_ALLOWED_SYMBOL_ROOTS must contain at least one directory.");
        }

        var fullPath = Path.GetFullPath(symbolDirectory);
        if (!options.AllowedSymbolRoots.Any(root => IsWithinRoot(fullPath, root)))
        {
            throw new UnauthorizedAccessException(
                "The symbol directory is outside BC_ALLOWED_SYMBOL_ROOTS.");
        }

        return fullPath;
    }

    private static IReadOnlyList<Guid> ValidateAppIds(IReadOnlyList<Guid>? appIds)
    {
        if (appIds is null || appIds.Count == 0)
        {
            throw new ArgumentException("At least one app ID is required.", nameof(appIds));
        }

        if (appIds.Any(appId => appId == Guid.Empty))
        {
            throw new ArgumentException("App IDs cannot be empty.", nameof(appIds));
        }

        if (appIds.Distinct().Count() != appIds.Count)
        {
            throw new ArgumentException("App IDs must be unique.", nameof(appIds));
        }

        return appIds.ToArray();
    }

    private static string SafeMessage(Exception exception)
    {
        var message = exception.Message.Replace(Environment.NewLine, " ", StringComparison.Ordinal);
        return message.Length <= 2048 ? message : message[..2048];
    }

    private void RejectReparsePoints(string destinationDirectory)
    {
        var allowedRoot = options.AllowedSymbolRoots
            .Select(Path.GetFullPath)
            .First(root => IsWithinRoot(destinationDirectory, root));
        for (var current = new DirectoryInfo(destinationDirectory);
             current is not null && IsWithinRoot(current.FullName, allowedRoot);
             current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    "Symbol directories cannot contain symbolic links or reparse points.");
            }

            if (PathsEqual(current.FullName, allowedRoot))
            {
                break;
            }
        }
    }

    private static void RejectReparsePoint(string filePath)
    {
        if ((File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException(
                "Existing symbol packages cannot be symbolic links or reparse points.");
        }
    }

    private static string SanitizeFileNamePart(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars()
            .Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*'])
            .ToHashSet();
        var sanitized = new string(value
            .Select(character => invalidCharacters.Contains(character) || char.IsControl(character)
                ? '_'
                : character)
            .ToArray())
            .Trim()
            .TrimEnd('.');
        if (sanitized.Length == 0)
        {
            return "Unknown";
        }

        return sanitized.Length <= 100 ? sanitized : sanitized[..100];
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static bool IsWithinRoot(string fullPath, string root)
    {
        var relativePath = Path.GetRelativePath(Path.GetFullPath(root), fullPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return !Path.IsPathRooted(relativePath)
            && !relativePath.Equals("..", comparison)
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", comparison);
    }

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).Equals(
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
