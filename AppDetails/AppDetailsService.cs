namespace BcDeployment;

public sealed class AppDetailsService(BusinessCentralAdminClient adminClient)
{
    public async Task<AppDetailsResult> GetAsync(
        Guid appId,
        string environmentName,
        CancellationToken cancellationToken)
    {
        if (appId == Guid.Empty)
        {
            throw new ArgumentException("App ID cannot be empty.", nameof(appId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        var environment = await adminClient.GetEnvironmentAsync(environmentName, cancellationToken);
        var app = (await adminClient.GetInstalledAppsAsync(environment.Name, cancellationToken))
            .FirstOrDefault(installedApp => installedApp.AppId == appId);
        var installations = app is null
            ? []
            : new[]
            {
                new AppInstallationDetails(
                    environment.Name,
                    environment.FriendlyName,
                    environment.Type,
                    environment.Status,
                    environment.ApplicationVersion,
                    environment.CountryCode,
                    app.Version.ToString(),
                    app.State,
                    app.AppType,
                    app.CanBeUninstalled,
                    app.LastUpdateOperationId,
                    app.LastUpdateAttemptResult,
                    app.LastUninstallOperationId,
                    app.LastUninstallAttemptResult)
            };

        return new AppDetailsResult(
            appId,
            app is not null,
            app?.Name,
            app?.Publisher,
            installations,
            []);
    }
}
