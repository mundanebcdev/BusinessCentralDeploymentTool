using System.Text.Json;

namespace BcDeployment;

public sealed record DeploymentJournalEvent(
    DateTimeOffset Timestamp,
    Guid DeploymentId,
    string Event,
    string? EnvironmentName = null,
    Guid? AppId = null,
    string? AppName = null,
    string? AppVersion = null,
    Guid? OperationId = null,
    string? Status = null,
    string? Message = null);

public sealed class DeploymentJournal(AdminCenterOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task AppendAsync(DeploymentJournalEvent journalEvent, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(options.JournalDirectory);
        var path = Path.Combine(options.JournalDirectory, $"{journalEvent.DeploymentId:D}.jsonl");
        var line = JsonSerializer.Serialize(journalEvent, JsonOptions) + Environment.NewLine;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(path, line, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
