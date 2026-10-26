using System.Collections.Concurrent;
using System.Diagnostics;
using ModelContextProtocol;

namespace BcDeployment;

public sealed class DeploymentOrchestrator(
    AdminCenterOptions options,
    PackageBundleReader packageReader,
    BusinessCentralAdminClient adminClient,
    DeploymentJournal journal)
{
    public async Task<BundleDeploymentResult> DeployAsync(
        string packagePath,
        IReadOnlyList<string> environmentNames,
        DeploymentSchedule deploymentSchedule,
        SyncMode syncMode,
        string languageId,
        bool installOrUpdateNeededDependencies,
        bool confirmProduction,
        int operationTimeoutSeconds,
        int pollIntervalSeconds,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken cancellationToken)
    {
        var requestedEnvironments = ValidateEnvironments(environmentNames);
        ValidateTiming(operationTimeoutSeconds, pollIntervalSeconds);
        var environments = await ValidateDeploymentTargetsAsync(
            requestedEnvironments,
            confirmProduction,
            cancellationToken);
        using var packageSet = packageReader.Prepare(packagePath);
        var deploymentId = Guid.NewGuid();

        var totalSteps = environments.Count * packageSet.Packages.Count;
        var completedSteps = 0;
        void ReportProgress(string message) => progress.Report(new ProgressNotificationValue
        {
            Progress = Volatile.Read(ref completedSteps),
            Total = totalSteps,
            Message = message
        });

        await journal.AppendAsync(new DeploymentJournalEvent(
            DateTimeOffset.UtcNow,
            deploymentId,
            "started",
            Status: "running"), cancellationToken);
        ReportProgress($"Validated {packageSet.Packages.Count} app package(s) for {environments.Count} environment(s). Starting deployment.");

        var results = new ConcurrentDictionary<string, EnvironmentDeploymentResult>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(
            environments,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = -1,
                CancellationToken = cancellationToken
            },
            async (environmentName, token) =>
            {
                results[environmentName] = await DeployEnvironmentAsync(
                    deploymentId,
                    environmentName,
                    packageSet.Packages,
                    deploymentSchedule,
                    syncMode,
                    languageId,
                    installOrUpdateNeededDependencies,
                    operationTimeoutSeconds,
                    pollIntervalSeconds,
                    ReportProgress,
                    () =>
                    {
                        var completed = Interlocked.Increment(ref completedSteps);
                        progress.Report(new ProgressNotificationValue
                        {
                            Progress = completed,
                            Total = totalSteps,
                            Message = $"Completed {completed} of {totalSteps} environment/app steps."
                        });
                    },
                    token);
            });

        var orderedResults = environments.Select(environment => results[environment]).ToArray();
        var result = new BundleDeploymentResult(
            deploymentId,
            OverallStatus(orderedResults),
            packageSet.Packages.Select(package => new PackageSummary(
                package.AppId,
                package.Name,
                package.Publisher,
                package.Version.ToString())).ToArray(),
            packageSet.ExternalDependencies.Select(dependency => new DependencySummary(
                dependency.AppId,
                dependency.Name,
                dependency.Publisher,
                dependency.MinimumVersion.ToString())).ToArray(),
            orderedResults);
        await journal.AppendAsync(new DeploymentJournalEvent(
            DateTimeOffset.UtcNow,
            deploymentId,
            "completed",
            Status: result.Status), CancellationToken.None);
        return result;
    }

    private async Task<IReadOnlyList<string>> ValidateDeploymentTargetsAsync(
        IReadOnlyList<string> requestedEnvironments,
        bool confirmProduction,
        CancellationToken cancellationToken)
    {
        var availableEnvironments = await adminClient.GetEnvironmentsAsync(cancellationToken);
        var environmentsByName = availableEnvironments.ToDictionary(
            environment => environment.Name,
            StringComparer.OrdinalIgnoreCase);
        var unknownEnvironments = requestedEnvironments
            .Where(name => !environmentsByName.ContainsKey(name))
            .ToArray();
        if (unknownEnvironments.Length > 0)
        {
            throw new ArgumentException(
                $"Unknown Business Central environment(s): {string.Join(", ", unknownEnvironments)}.",
                nameof(requestedEnvironments));
        }

        var productionEnvironments = requestedEnvironments
            .Select(name => environmentsByName[name])
            .Where(environment => environment.Type.Equals("Production", StringComparison.OrdinalIgnoreCase))
            .Select(environment => environment.Name)
            .ToArray();
        if (productionEnvironments.Length > 0)
        {
            if (!options.AllowProductionDeployment)
            {
                throw new InvalidOperationException(
                    $"Deployment to Production environments is disabled: {string.Join(", ", productionEnvironments)}. "
                    + "Set BC_ALLOW_PRODUCTION_DEPLOYMENT=true on the MCP server to enable it.");
            }

            if (!confirmProduction)
            {
                throw new ArgumentException(
                    $"confirmProduction must be true to deploy to Production environments: {string.Join(", ", productionEnvironments)}.",
                    nameof(confirmProduction));
            }
        }

        return requestedEnvironments.Select(name => environmentsByName[name].Name).ToArray();
    }

    private async Task<EnvironmentDeploymentResult> DeployEnvironmentAsync(
        Guid deploymentId,
        string environmentName,
        IReadOnlyList<AppPackage> packages,
        DeploymentSchedule deploymentSchedule,
        SyncMode syncMode,
        string languageId,
        bool installOrUpdateNeededDependencies,
        int operationTimeoutSeconds,
        int pollIntervalSeconds,
        Action<string> reportProgress,
        Action stepCompleted,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<InstalledApp> installedApps;
        try
        {
            reportProgress($"{environmentName}: checking installed app versions.");
            installedApps = await adminClient.GetInstalledAppsAsync(environmentName, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            reportProgress($"{environmentName}: preflight failed before upload.");
            var blockedApps = packages.Select(package =>
            {
                stepCompleted();
                return new AppDeploymentResult(
                    package.AppId,
                    package.Name,
                    package.Version.ToString(),
                    "blocked",
                    null,
                    null,
                    "Environment preflight failed before any package was uploaded.");
            }).ToArray();
            await journal.AppendAsync(new DeploymentJournalEvent(
                DateTimeOffset.UtcNow,
                deploymentId,
                "environmentPreflightFailed",
                environmentName,
                Status: "failed",
                Message: SafeMessage(exception)), CancellationToken.None);
            return new EnvironmentDeploymentResult(
                environmentName,
                "failed",
                blockedApps,
                SafeMessage(exception));
        }

        var results = new List<AppDeploymentResult>(packages.Count);
        for (var index = 0; index < packages.Count; index++)
        {
            var package = packages[index];
            var installed = installedApps.FirstOrDefault(app => app.AppId == package.AppId);
            if (installed is not null && installed.Version >= package.Version)
            {
                reportProgress($"{environmentName}: skipping {package.Name} {package.Version}; version {installed.Version} is already installed.");
                results.Add(new AppDeploymentResult(
                    package.AppId,
                    package.Name,
                    package.Version.ToString(),
                    "skipped",
                    null,
                    null,
                    $"Version {installed.Version} is already installed."));
                stepCompleted();
                continue;
            }

            try
            {
                reportProgress($"{environmentName}: uploading {package.Name} {package.Version}.");
                var operation = await adminClient.PublishPteAsync(
                    package,
                    environmentName,
                    deploymentSchedule,
                    syncMode,
                    languageId,
                    installOrUpdateNeededDependencies,
                    cancellationToken);
                await journal.AppendAsync(new DeploymentJournalEvent(
                    DateTimeOffset.UtcNow,
                    deploymentId,
                    "operationAccepted",
                    environmentName,
                    package.AppId,
                    package.Name,
                    package.Version.ToString(),
                    operation.OperationId,
                    operation.Status), CancellationToken.None);
                reportProgress($"{environmentName}: {package.Name} operation {operation.OperationId:D} accepted with status '{operation.Status}'.");

                if (deploymentSchedule == DeploymentSchedule.Immediate)
                {
                    var waitResult = await WaitForOperationAsync(
                        environmentName,
                        operation,
                        operationTimeoutSeconds,
                        pollIntervalSeconds,
                        (currentOperation, elapsed) => reportProgress(
                            $"{environmentName}: {package.Name} is '{currentOperation.Status}' after {elapsed:mm\\:ss} (operation {currentOperation.OperationId:D})."),
                        cancellationToken);
                    operation = waitResult.Operation;
                    if (waitResult.TimedOut)
                    {
                        results.Add(new AppDeploymentResult(
                            package.AppId,
                            package.Name,
                            package.Version.ToString(),
                            "timedOut",
                            operation.OperationId,
                            operation.Status,
                            $"Operation did not finish within {operationTimeoutSeconds} seconds.")
                        {
                            ScheduleKind = operation.ScheduleKind ?? deploymentSchedule.ToString(),
                            Operation = operation
                        });
                        await JournalOperationAsync(deploymentId, environmentName, package, operation, "timedOut");
                        stepCompleted();
                        AddBlockedPackages(packages, index + 1, results, stepCompleted);
                        break;
                    }
                }

                var deploymentStatus = deploymentSchedule == DeploymentSchedule.Immediate
                    ? operation.Status
                    : "scheduled";
                results.Add(new AppDeploymentResult(
                    package.AppId,
                    package.Name,
                    package.Version.ToString(),
                    deploymentStatus,
                    operation.OperationId,
                    operation.Status,
                    operation.ErrorMessage)
                {
                    ScheduleKind = operation.ScheduleKind ?? deploymentSchedule.ToString(),
                    Operation = operation
                });
                await JournalOperationAsync(deploymentId, environmentName, package, operation, deploymentStatus);
                stepCompleted();

                if (deploymentSchedule == DeploymentSchedule.Immediate
                    && !operation.Status.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    AddBlockedPackages(packages, index + 1, results, stepCompleted);
                    break;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                results.Add(new AppDeploymentResult(
                    package.AppId,
                    package.Name,
                    package.Version.ToString(),
                    "failed",
                    null,
                    null,
                    SafeMessage(exception)));
                stepCompleted();
                await journal.AppendAsync(new DeploymentJournalEvent(
                    DateTimeOffset.UtcNow,
                    deploymentId,
                    "operationFailed",
                    environmentName,
                    package.AppId,
                    package.Name,
                    package.Version.ToString(),
                    Status: "failed",
                    Message: SafeMessage(exception)), CancellationToken.None);
                AddBlockedPackages(packages, index + 1, results, stepCompleted);
                break;
            }
        }

        var status = EnvironmentStatus(results);
        return new EnvironmentDeploymentResult(environmentName, status, results, null);
    }

    private async Task<OperationWaitResult> WaitForOperationAsync(
        string environmentName,
        DeploymentOperation operation,
        int timeoutSeconds,
        int pollIntervalSeconds,
        Action<DeploymentOperation, TimeSpan> operationPolled,
        CancellationToken cancellationToken)
    {
        if (operation.IsTerminal)
        {
            return new OperationWaitResult(operation, false);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), timeout.Token);
                operation = await adminClient.GetOperationAsync(
                    environmentName,
                    operation.AppId,
                    operation.OperationId,
                    timeout.Token);
                operationPolled(operation, Stopwatch.GetElapsedTime(startedAt));
                if (operation.IsTerminal)
                {
                    return new OperationWaitResult(operation, false);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new OperationWaitResult(operation, true);
        }
    }

    private Task JournalOperationAsync(
        Guid deploymentId,
        string environmentName,
        AppPackage package,
        DeploymentOperation operation,
        string status) =>
        journal.AppendAsync(new DeploymentJournalEvent(
            DateTimeOffset.UtcNow,
            deploymentId,
            "operationUpdated",
            environmentName,
            package.AppId,
            package.Name,
            package.Version.ToString(),
            operation.OperationId,
            status,
            operation.ErrorMessage), CancellationToken.None);

    private static void AddBlockedPackages(
        IReadOnlyList<AppPackage> packages,
        int startIndex,
        ICollection<AppDeploymentResult> results,
        Action stepCompleted)
    {
        for (var index = startIndex; index < packages.Count; index++)
        {
            var package = packages[index];
            results.Add(new AppDeploymentResult(
                package.AppId,
                package.Name,
                package.Version.ToString(),
                "blocked",
                null,
                null,
                "A package earlier in the dependency order did not succeed."));
            stepCompleted();
        }
    }

    private static IReadOnlyList<string> ValidateEnvironments(IReadOnlyList<string> environmentNames)
    {
        var environments = environmentNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (environments.Length == 0)
        {
            throw new ArgumentException("At least one environment name is required.", nameof(environmentNames));
        }

        if (environments.Length > 20)
        {
            throw new ArgumentException("A single deployment can target at most 20 environments.", nameof(environmentNames));
        }

        return environments;
    }

    private static void ValidateTiming(int timeoutSeconds, int pollIntervalSeconds)
    {
        if (timeoutSeconds is < 30 or > 7200)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Operation timeout must be between 30 and 7200 seconds.");
        }

        if (pollIntervalSeconds is < 2 or > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(pollIntervalSeconds), "Poll interval must be between 2 and 60 seconds.");
        }
    }

    private static string EnvironmentStatus(IReadOnlyCollection<AppDeploymentResult> results)
    {
        if (results.Any(result => result.Status == "timedOut"))
        {
            return "timedOut";
        }

        if (results.Any(result => result.Status is "failed" or "canceled" or "blocked"))
        {
            return "failed";
        }

        return results.Any(result => result.Status == "scheduled") ? "scheduled" : "succeeded";
    }

    private static string OverallStatus(IReadOnlyCollection<EnvironmentDeploymentResult> results)
    {
        if (results.All(result => result.Status == "succeeded"))
        {
            return "succeeded";
        }

        if (results.All(result => result.Status == "scheduled"))
        {
            return "scheduled";
        }

        if (results.All(result => result.Status == "timedOut"))
        {
            return "timedOut";
        }

        if (results.All(result => result.Status == "failed"))
        {
            return "failed";
        }

        return "partial";
    }

    private static string SafeMessage(Exception exception)
    {
        var message = exception.Message.Replace(Environment.NewLine, " ", StringComparison.Ordinal);
        return message.Length <= 2048 ? message : message[..2048];
    }

    private sealed record OperationWaitResult(DeploymentOperation Operation, bool TimedOut);
}
