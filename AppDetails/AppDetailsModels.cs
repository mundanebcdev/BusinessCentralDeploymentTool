namespace BcDeployment;

public sealed record AppInstallationDetails(
    string EnvironmentName,
    string EnvironmentFriendlyName,
    string EnvironmentType,
    string EnvironmentStatus,
    string EnvironmentVersion,
    string CountryCode,
    string AppVersion,
    string AppState,
    string AppType,
    bool CanBeUninstalled,
    Guid? LastUpdateOperationId,
    string? LastUpdateAttemptResult,
    Guid? LastUninstallOperationId,
    string? LastUninstallAttemptResult);

public sealed record AppLookupError(
    string EnvironmentName,
    string Message);

public sealed record AppDetailsResult(
    Guid AppId,
    bool Found,
    string? Name,
    string? Publisher,
    IReadOnlyList<AppInstallationDetails> Installations,
    IReadOnlyList<AppLookupError> LookupErrors);
