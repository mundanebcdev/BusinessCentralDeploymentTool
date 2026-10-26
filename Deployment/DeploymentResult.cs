namespace BcDeployment;

public sealed record PackageSummary(
    Guid AppId,
    string Name,
    string Publisher,
    string Version);

public sealed record DependencySummary(
    Guid AppId,
    string Name,
    string Publisher,
    string MinimumVersion);

public sealed record AppDeploymentResult(
    Guid AppId,
    string Name,
    string Version,
    string Status,
    Guid? OperationId,
    string? AdminCenterStatus,
    string? Message)
{
    public string? ScheduleKind { get; init; }

    public DeploymentOperation? Operation { get; init; }
}

public sealed record EnvironmentDeploymentResult(
    string EnvironmentName,
    string Status,
    IReadOnlyList<AppDeploymentResult> Apps,
    string? Message);

public sealed record BundleDeploymentResult(
    Guid DeploymentId,
    string Status,
    IReadOnlyList<PackageSummary> PackageOrder,
    IReadOnlyList<DependencySummary> ExternalDependencies,
    IReadOnlyList<EnvironmentDeploymentResult> Environments);
