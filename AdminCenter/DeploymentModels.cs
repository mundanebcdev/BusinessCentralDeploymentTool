using System.Text.Json.Serialization;

namespace BcDeployment;

public enum DeploymentSchedule
{
    Immediate,
    UpdateWindow,
    NextMinorUpdate,
    NextMajorUpdate
}

public enum SyncMode
{
    Add,
    ForceSync
}

public sealed record DeploymentOperation(
    Guid OperationId,
    Guid AppId,
    string EnvironmentName,
    string Type,
    string Status,
    string? SourceVersion,
    string? TargetVersion,
    DateTimeOffset? CreatedOn,
    DateTimeOffset? StartedOn,
    DateTimeOffset? CompletedOn,
    string? ErrorMessage,
    string? ScheduleKind)
{
    public bool IsTerminal => Status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
        || Status.Equals("failed", StringComparison.OrdinalIgnoreCase)
        || Status.Equals("canceled", StringComparison.OrdinalIgnoreCase)
        || Status.Equals("skipped", StringComparison.OrdinalIgnoreCase);
}

public sealed record InstalledApp(
    Guid AppId,
    string Name,
    string Publisher,
    Version Version,
    string State,
    Guid? LastUpdateOperationId,
    string? LastUpdateAttemptResult,
    Guid? LastUninstallOperationId,
    string? LastUninstallAttemptResult,
    string AppType,
    bool CanBeUninstalled);

public sealed record BusinessCentralEnvironment(
    string Name,
    [property: JsonIgnore] string FriendlyName,
    string Type,
    string CountryCode,
    string Status,
    string ApplicationVersion);
