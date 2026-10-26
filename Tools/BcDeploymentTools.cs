using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BcDeployment;

[McpServerToolType]
public sealed class BcDeploymentTools(
    DeploymentOrchestrator orchestrator,
    AppDetailsService appDetailsService,
    BusinessCentralAdminClient adminClient,
    SymbolDownloadService symbolDownloadService)
{
    [McpServerTool(Name = "bc_list_environments")]
    [Description("Lists Business Central environments in the tenant, optionally filtered by exact environment name and country code. Returns environment name, type, country, status, and Business Central version.")]
    public async Task<IReadOnlyList<BusinessCentralEnvironment>> ListEnvironmentsAsync(
        [Description("Optional exact Business Central environment name. Omit to list all environments.")] string? environmentName = null,
        [Description("Optional exact ISO 3166-1 alpha-2 country code, such as NL. Omit to include all countries.")] string? countryCode = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedCountryCode = string.IsNullOrWhiteSpace(countryCode)
            ? null
            : countryCode.Trim();
        var environments = await adminClient.GetEnvironmentsAsync(cancellationToken);
        return environments
            .Where(environment => environmentName is null
                || environment.Name.Equals(environmentName, StringComparison.OrdinalIgnoreCase))
            .Where(environment => normalizedCountryCode is null
                || environment.CountryCode.Equals(normalizedCountryCode, StringComparison.OrdinalIgnoreCase))
            .OrderBy(environment => environment.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    [McpServerTool(Name = "bc_get_app_details")]
    [Description("Gets the current installed version and available Admin Center details for an app ID in one Business Central environment.")]
    public Task<AppDetailsResult> GetAppDetailsAsync(
        [Description("Business Central app ID.")] Guid appId,
        [Description("Business Central environment name to look up.")] string environmentName,
        CancellationToken cancellationToken = default) =>
        appDetailsService.GetAsync(appId, environmentName, cancellationToken);

    [McpServerTool(Name = "bc_get_operation_details")]
    [Description("Gets one Business Central Admin Center app operation, including creation, start, and completion timestamps, versions, status, type, schedule, and any error message. Use as a recovery fallback when a deployment call was canceled or its result was lost.")]
    public Task<DeploymentOperation> GetOperationDetailsAsync(
        [Description("Business Central environment name containing the app operation.")] string environmentName,
        [Description("Business Central app ID associated with the operation.")] Guid appId,
        [Description("Business Central Admin Center operation ID to retrieve.")] Guid operationId,
        CancellationToken cancellationToken = default) =>
        adminClient.GetOperationAsync(environmentName, appId, operationId, cancellationToken);

    [McpServerTool(Name = "bc_download_symbol")]
    [Description("Downloads installed Business Central app symbol packages by app ID from a named cloud environment into an allowlisted AL symbol cache. Downloads no more than 4 packages concurrently. Supports Sandbox and Production environments.")]
    public Task<SymbolDownloadBatchResult> DownloadSymbolAsync(
        [Description("Business Central app IDs to download. Supply a non-empty array of unique IDs.")] Guid[] appIds,
        [Description("Exact Business Central environment name to download from.")] string environmentName,
        [Description("Destination AL symbol directory. Required and must be within BC_ALLOWED_SYMBOL_ROOTS.")] string symbolDirectory,
        [Description("Replace an existing same-name package only when its content differs. Identical packages are returned as alreadyPresent.")] bool force = false,
        [Description("Maximum symbol packages downloaded at the same time. Must be between 1 and 4.")] int maxParallelDownloads = 4,
        CancellationToken cancellationToken = default) =>
        symbolDownloadService.DownloadManyAsync(
            appIds,
            environmentName,
            symbolDirectory,
            force,
            maxParallelDownloads,
            cancellationToken);

    [McpServerTool(Name = "bc_deploy_pte")]
    [Description("Deploys one .app file or a ZIP of dependent .app files to one or more Business Central environments. Production targets require the server to be started with BC_ALLOW_PRODUCTION_DEPLOYMENT=true and the caller to pass confirmProduction. Apps are ordered by dependency, environments run in parallel, and immediate deployments are monitored to completion in this single tool call.")]
    public Task<BundleDeploymentResult> DeployPteAsync(
        [Description("Absolute or workspace-relative path to an .app file or ZIP containing .app files.")] string packagePath,
        [Description("Business Central environment names. Deployments run in parallel across environments.")] string[] environmentNames,
        [Description("When to deploy. ZIP bundle apps are submitted in dependency order for every schedule. Immediate waits for each prerequisite to succeed before submitting its dependents; deferred schedules return after Admin Center accepts each operation. Admin Center permits NextMinorUpdate and NextMajorUpdate only for updates to installed PTEs.")] DeploymentSchedule deploymentSchedule = DeploymentSchedule.Immediate,
        [Description("Schema synchronization mode. ForceSync can cause data loss and must be selected deliberately.")] SyncMode syncMode = SyncMode.Add,
        [Description("Language used during installation, as a BCP 47 code.")] string languageId = "en-US",
        [Description("Must be true to accept the app provider's EULA and Microsoft's documented marketplace terms.")] bool acceptIsvEula = false,
        [Description("Allow Business Central to install or update available dependencies.")] bool installOrUpdateNeededDependencies = true,
        [Description("Must be true when any target is a Production environment. Set only when the user explicitly authorized a Production deployment.")] bool confirmProduction = false,
        [Description("Server-side maximum seconds to poll each immediate app operation. The MCP client may have a shorter timeout.")] int operationTimeoutSeconds = 1800,
        [Description("Seconds between Admin Center status checks.")] int pollIntervalSeconds = 10,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!acceptIsvEula)
        {
            throw new ArgumentException(
                "acceptIsvEula must be true. Review the Admin Center API app-management terms before deploying.",
                nameof(acceptIsvEula));
        }

        return orchestrator.DeployAsync(
            packagePath,
            environmentNames,
            deploymentSchedule,
            syncMode,
            languageId,
            installOrUpdateNeededDependencies,
            confirmProduction,
            operationTimeoutSeconds,
            pollIntervalSeconds,
            progress ?? NullProgress.Instance,
            cancellationToken);
    }

    private sealed class NullProgress : IProgress<ProgressNotificationValue>
    {
        public static NullProgress Instance { get; } = new();

        public void Report(ProgressNotificationValue value)
        {
        }
    }
}
