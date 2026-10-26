using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Azure.Core;

namespace BcDeployment;

public sealed class BusinessCentralDevClient(
    HttpClient httpClient,
    TokenCredential credential,
    AdminCenterOptions options)
{
    public async Task<long> DownloadPackageAsync(
        Guid appId,
        string environmentName,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (appId == Guid.Empty)
        {
            throw new ArgumentException("App ID cannot be empty.", nameof(appId));
        }

        if (string.IsNullOrWhiteSpace(environmentName))
        {
            throw new ArgumentException("Environment name is required.", nameof(environmentName));
        }

        var tenantId = string.IsNullOrWhiteSpace(options.TenantId)
            ? throw new InvalidOperationException("BC_TENANT_ID is required to download symbols.")
            : options.TenantId;
        var requestPath = $"v2.0/{Uri.EscapeDataString(tenantId)}/{Uri.EscapeDataString(environmentName)}/dev/packages?appId={appId:D}";

        using var response = await SendAsync(requestPath, cancellationToken);
        if (response.Content.Headers.ContentLength is > 0 and var contentLength
            && contentLength > maximumBytes)
        {
            throw new InvalidDataException(
                $"The symbol package exceeds the {maximumBytes / (1024 * 1024)} MB download limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920];
        long totalBytes = 0;
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytes += bytesRead;
            if (totalBytes > maximumBytes)
            {
                throw new InvalidDataException(
                    $"The symbol package exceeds the {maximumBytes / (1024 * 1024)} MB download limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }

        return totalBytes;
    }

    private async Task<HttpResponseMessage> SendAsync(
        string requestPath,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 4;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestPath);
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
            catch (HttpRequestException) when (attempt < maximumAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken);
                continue;
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            if (attempt < maximumAttempts && IsRetryable(response.StatusCode))
            {
                var delay = RetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            var statusCode = response.StatusCode;
            var reasonPhrase = response.ReasonPhrase;
            var body = await ReadErrorBodyAsync(response, cancellationToken);
            response.Dispose();
            throw new HttpRequestException(
                $"Business Central development endpoint returned {(int)statusCode} ({reasonPhrase}): {body}",
                null,
                statusCode);
        }

        throw new InvalidOperationException("The symbol download exhausted its retry attempts.");
    }

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? delay = retryAfter?.Delta;
        if (delay is null && retryAfter?.Date is { } date)
        {
            delay = date - DateTimeOffset.UtcNow;
        }

        return TimeSpan.FromSeconds(Math.Clamp(
            (delay ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1))).TotalSeconds,
            0,
            30));
    }

    private static async Task<string> ReadErrorBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        const int maximumErrorBytes = 4096;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[maximumErrorBytes + 1];
        var bytesRead = await stream.ReadAtLeastAsync(
            buffer,
            minimumBytes: 1,
            throwOnEndOfStream: false,
            cancellationToken);
        var truncated = bytesRead > maximumErrorBytes;
        return Encoding.UTF8.GetString(buffer, 0, Math.Min(bytesRead, maximumErrorBytes))
            + (truncated ? "..." : string.Empty);
    }
}
