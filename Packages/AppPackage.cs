namespace BcDeployment;

public sealed record AppDependency(
    Guid AppId,
    string Name,
    string Publisher,
    Version MinimumVersion);

public sealed record AppPackage(
    string FilePath,
    string FileName,
    Guid AppId,
    string Name,
    string Publisher,
    Version Version,
    IReadOnlyList<AppDependency> Dependencies);

public sealed record PreparedPackageSet(
    IReadOnlyList<AppPackage> Packages,
    IReadOnlyList<AppDependency> ExternalDependencies,
    string? TemporaryDirectory) : IDisposable
{
    public void Dispose()
    {
        if (TemporaryDirectory is not null && Directory.Exists(TemporaryDirectory))
        {
            Directory.Delete(TemporaryDirectory, true);
        }
    }
}
