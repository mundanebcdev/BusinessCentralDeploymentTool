namespace BcDeployment;

public sealed record SymbolDownloadResult(
    string Status,
    Guid AppId,
    string Name,
    string Publisher,
    string Version,
    string EnvironmentName,
    string FilePath,
    long SizeBytes,
    string Sha256,
    IReadOnlyList<DependencySummary> Dependencies);

public sealed record SymbolDownloadItemResult(
    Guid AppId,
    string Status,
    SymbolDownloadResult? Symbol,
    string? Message);

public sealed record SymbolDownloadBatchResult(
    string Status,
    string EnvironmentName,
    IReadOnlyList<SymbolDownloadItemResult> Apps);
