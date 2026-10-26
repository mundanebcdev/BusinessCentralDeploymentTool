using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;

namespace BcDeployment;

public sealed class BusinessCentralAdminClient(
    HttpClient httpClient,
    TokenCredential credential,
    AdminCenterOptions options)
{
    private const long MaximumAppBytes = 50L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<DeploymentOperation> PublishPteAsync(
        string appPath,
        string environmentName,
        DeploymentSchedule deploymentSchedule,
        SyncMode syncMode,
        string languageId,
        bool installOrUpdateNeededDependencies,
        CancellationToken cancellationToken)
    {
        var appFile = ValidateAppFile(appPath);
        ValidateEnvironmentName(environmentName);

        return await PublishPteAsync(
            appFile.OpenRead,
            appFile.Name,
            environmentName,
            deploymentSchedule,
            syncMode,
            languageId,
            installOrUpdateNeededDependencies,
            cancellationToken);
    }

    public async Task<DeploymentOperation> PublishPteAsync(
        AppPackage package,
        string environmentName,
        DeploymentSchedule deploymentSchedule,
        SyncMode syncMode,
        string languageId,
        bool installOrUpdateNeededDependencies,
        CancellationToken cancellationToken)
    {
        ValidateEnvironmentName(environmentName);
        return await PublishPteAsync(
            () => File.OpenRead(package.FilePath),
            package.FileName,
            environmentName,
            deploymentSchedule,
            syncMode,
            languageId,
            installOrUpdateNeededDependencies,
            cancellationToken);
    }

    public async Task<IReadOnlyList<InstalledApp>> GetInstalledAppsAsync(
        string environmentName,
        CancellationToken cancellationToken)
    {
        ValidateEnvironmentName(environmentName);
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, BuildPath(environmentName, "apps")),
            RetryMode.Idempotent,
            cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<InstalledAppsResponse>(
            stream,
            JsonOptions,
            cancellationToken);

        return result?.Value.Select(app => new InstalledApp(
            app.Id ?? app.AppId ?? Guid.Empty,
                app.Name ?? string.Empty,
                app.Publisher ?? string.Empty,
                ParseVersion(app.Version),
                app.State ?? string.Empty,
                app.LastUpdateOperationId,
                app.LastUpdateAttemptResult,
                app.LastUninstallOperationId,
                app.LastUninstallAttemptResult,
                app.AppType ?? string.Empty,
                app.CanBeUninstalled))
            .ToArray()
            ?? [];
    }

    public async Task<IReadOnlyList<BusinessCentralEnvironment>> GetEnvironmentsAsync(
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"admin/{options.ApiVersion}/applications/{options.ApplicationFamily}/environments"),
            RetryMode.Idempotent,
            cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<EnvironmentsResponse>(
            stream,
            JsonOptions,
            cancellationToken);

        return result?.Value.Select(MapEnvironment)
            .Where(environment => !string.IsNullOrWhiteSpace(environment.Name))
            .ToArray()
            ?? [];
    }

    public async Task<BusinessCentralEnvironment> GetEnvironmentAsync(
        string environmentName,
        CancellationToken cancellationToken)
    {
        ValidateEnvironmentName(environmentName);
        using var response = await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"admin/{options.ApiVersion}/applications/{options.ApplicationFamily}/environments/{Uri.EscapeDataString(environmentName)}"),
            RetryMode.Idempotent,
            cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<JsonElement>(
            stream,
            JsonOptions,
            cancellationToken);
        var environment = result.TryGetProperty("value", out var value)
            ? value.EnumerateArray().Select(item => item.Deserialize<EnvironmentDto>(JsonOptions)).SingleOrDefault()
            : result.Deserialize<EnvironmentDto>(JsonOptions);
        if (environment is null)
        {
            throw new InvalidDataException($"Environment '{environmentName}' was not returned by the Admin Center API.");
        }

        return MapEnvironment(environment);
    }

    private static BusinessCentralEnvironment MapEnvironment(EnvironmentDto environment) =>
        new(
            environment.Name ?? string.Empty,
            environment.FriendlyName ?? string.Empty,
            environment.Type ?? string.Empty,
            environment.CountryCode ?? string.Empty,
            environment.Status ?? string.Empty,
            environment.VersionDetails?.Version ?? string.Empty);

    private async Task<DeploymentOperation> PublishPteAsync(
        Func<Stream> openAppStream,
        string fileName,
        string environmentName,
        DeploymentSchedule deploymentSchedule,
        SyncMode syncMode,
        string languageId,
        bool installOrUpdateNeededDependencies,
        CancellationToken cancellationToken)
    {

        using var response = await SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    BuildPath(environmentName, "apps/pteInstall"));
                var form = new MultipartFormDataContent();
                var fileContent = new StreamContent(openAppStream());
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                form.Add(fileContent, "extensionFile", fileName);
                form.Add(new StringContent(deploymentSchedule.ToString()), "deploymentSchedule");
                form.Add(new StringContent(syncMode.ToString()), "syncMode");
                form.Add(new StringContent(languageId), "languageId");
                form.Add(new StringContent("true"), "acceptIsvEula");
                form.Add(
                    new StringContent(installOrUpdateNeededDependencies ? "true" : "false"),
                    "installOrUpdateNeededDependencies");
                request.Content = form;
                return request;
            },
            RetryMode.Upload,
            cancellationToken);
        var operation = await ReadOperationAsync(response, cancellationToken);
        return Map(operation, environmentName, operation.AppId);
    }

    public async Task<DeploymentOperation> GetOperationAsync(
        string environmentName,
        Guid appId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        ValidateEnvironmentName(environmentName);
        if (appId == Guid.Empty)
        {
            throw new ArgumentException("App ID cannot be empty.", nameof(appId));
        }

        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Operation ID cannot be empty.", nameof(operationId));
        }

        using var response = await SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                BuildPath(environmentName, $"apps/{appId:D}/operations/{operationId:D}")),
            RetryMode.Idempotent,
            cancellationToken);
        var operation = await ReadOperationAsync(response, cancellationToken);
        return Map(operation, environmentName, appId);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> createRequest,
        RetryMode retryMode,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 4;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            using var request = createRequest();
            var token = await credential.GetTokenAsync(
                new TokenRequestContext([options.Scope]),
                cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            catch (HttpRequestException) when (retryMode == RetryMode.Idempotent && attempt < maximumAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken);
                continue;
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            if (attempt < maximumAttempts && IsRetryable(response.StatusCode, retryMode))
            {
                var delay = RetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            var statusCode = response.StatusCode;
            var reasonPhrase = response.ReasonPhrase;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();
            throw new HttpRequestException(
                $"Business Central Admin Center API returned {(int)statusCode} ({reasonPhrase}): {Truncate(body, 4096)}",
                null,
                statusCode);
        }

        throw new InvalidOperationException("The Admin Center request exhausted its retry attempts.");
    }

    private static bool IsRetryable(System.Net.HttpStatusCode statusCode, RetryMode retryMode) =>
        statusCode == System.Net.HttpStatusCode.TooManyRequests
        || statusCode == System.Net.HttpStatusCode.BadGateway
        || statusCode == System.Net.HttpStatusCode.ServiceUnavailable
        || statusCode == System.Net.HttpStatusCode.GatewayTimeout
        || (retryMode == RetryMode.Idempotent && (int)statusCode >= 500);

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? delay = retryAfter?.Delta;
        if (delay is null && retryAfter?.Date is { } date)
        {
            delay = date - DateTimeOffset.UtcNow;
        }

        var effectiveDelay = delay ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
        return TimeSpan.FromSeconds(Math.Clamp(effectiveDelay.TotalSeconds, 0, 30));
    }

    private async Task<OperationDto> ReadOperationAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.TryGetProperty("value", out var values))
        {
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("The Admin Center API returned no matching deployment operation.");
            }

            root = values[0];
        }

        return root.Deserialize<OperationDto>(JsonOptions)
            ?? throw new InvalidOperationException("The Admin Center API returned an invalid deployment operation.");
    }

    private FileInfo ValidateAppFile(string appPath)
    {
        if (string.IsNullOrWhiteSpace(appPath))
        {
            throw new ArgumentException("App path is required.", nameof(appPath));
        }

        var fullPath = Path.GetFullPath(appPath);
        if (!options.AllowedAppRoots.Any(root => IsWithinRoot(fullPath, root)))
        {
            throw new UnauthorizedAccessException(
                "The app path is outside BC_ALLOWED_APP_ROOTS.");
        }

        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The .app package was not found.");
        }

        if (!file.Extension.Equals(".app", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The package must have an .app extension.", nameof(appPath));
        }

        if (file.Length is <= 0 or > MaximumAppBytes)
        {
            throw new ArgumentException("The .app package must be between 1 byte and 50 MB.", nameof(appPath));
        }

        return file;
    }

    private static bool IsWithinRoot(string fullPath, string root)
    {
        var relativePath = Path.GetRelativePath(root, fullPath);
        return !Path.IsPathRooted(relativePath)
            && relativePath != ".."
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static void ValidateEnvironmentName(string environmentName)
    {
        if (string.IsNullOrWhiteSpace(environmentName))
        {
            throw new ArgumentException("Environment name is required.", nameof(environmentName));
        }
    }

    private string BuildPath(string environmentName, string suffix) =>
        $"admin/{options.ApiVersion}/applications/{options.ApplicationFamily}/environments/{Uri.EscapeDataString(environmentName)}/{suffix}";

    private static DeploymentOperation Map(
        OperationDto operation,
        string environmentName,
        Guid fallbackAppId) =>
        new(
            operation.Id,
            operation.AppId == Guid.Empty ? fallbackAppId : operation.AppId,
            environmentName,
            operation.Type ?? "install",
            (operation.Status ?? "unknown").ToLowerInvariant(),
            operation.SourceAppVersion ?? operation.SourceVersion,
            operation.TargetAppVersion ?? operation.TargetVersion,
            operation.CreatedOn,
            operation.StartedOn,
            operation.CompletedOn,
            string.IsNullOrWhiteSpace(operation.ErrorMessage) ? null : operation.ErrorMessage,
            operation.ScheduleKind);

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static Version ParseVersion(string? value) =>
        Version.TryParse(value, out var version) ? version : new Version();

    private sealed class OperationDto
    {
        public Guid Id { get; init; }
        public Guid AppId { get; init; }
        public string? Type { get; init; }
        public string? Status { get; init; }
        public string? SourceAppVersion { get; init; }
        public string? TargetAppVersion { get; init; }
        public string? SourceVersion { get; init; }
        public string? TargetVersion { get; init; }
        public DateTimeOffset? CreatedOn { get; init; }
        public DateTimeOffset? StartedOn { get; init; }
        public DateTimeOffset? CompletedOn { get; init; }
        public string? ErrorMessage { get; init; }
        public string? ScheduleKind { get; init; }
    }

    private sealed class InstalledAppsResponse
    {
        public InstalledAppDto[] Value { get; init; } = [];
    }

    private sealed class InstalledAppDto
    {
        public Guid? Id { get; init; }
        public Guid? AppId { get; init; }
        public string? Name { get; init; }
        public string? Publisher { get; init; }
        public string? Version { get; init; }
        public string? State { get; init; }
        public Guid? LastUpdateOperationId { get; init; }
        public string? LastUpdateAttemptResult { get; init; }
        public Guid? LastUninstallOperationId { get; init; }
        public string? LastUninstallAttemptResult { get; init; }
        public string? AppType { get; init; }
        public bool CanBeUninstalled { get; init; }
    }

    private sealed class EnvironmentsResponse
    {
        public EnvironmentDto[] Value { get; init; } = [];
    }

    private sealed class EnvironmentDto
    {
        public string? Name { get; init; }
        public string? FriendlyName { get; init; }
        public string? Type { get; init; }
        public string? CountryCode { get; init; }
        public string? Status { get; init; }
        public VersionDetailsDto? VersionDetails { get; init; }
    }

    private sealed class VersionDetailsDto
    {
        public string? Version { get; init; }
    }

    private enum RetryMode
    {
        Idempotent,
        Upload
    }
}
