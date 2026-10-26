using System.Net;
using System.Net.Http.Headers;
using Azure.Core;

namespace BcDeployment.Tests;

[TestClass]
public sealed class SymbolDownloadServiceTests : IDisposable
{
    private readonly TestContext _testContext;
    private readonly string _root;

    public SymbolDownloadServiceTests(TestContext testContext)
    {
        _testContext = testContext;
        _root = Path.Combine(Path.GetTempPath(), $"bc-symbol-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task DownloadAsync_ValidPackage_WritesCanonicalFileAndReturnsMetadata()
    {
        var appId = Guid.NewGuid();
        var dependencyId = Guid.NewGuid();
        var packageBytes = CreatePackageBytes(
            appId,
            "My App",
            new Version(2, 1, 0, 0),
            (dependencyId, "Base App", new Version(1, 0, 0, 0)));
        Uri? requestUri = null;
        AuthenticationHeaderValue? authorization = null;
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            requestUri = request.RequestUri;
            authorization = request.Headers.Authorization;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(packageBytes)
            });
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);

        var result = await service.DownloadAsync(
            appId,
            "BC 28 DE",
            _root,
            force: false,
            _testContext.CancellationToken);

        Assert.IsNotNull(requestUri);
        Assert.AreEqual(
            $"https://api.businesscentral.dynamics.com/v2.0/test-tenant/BC%2028%20DE/dev/packages?appId={appId:D}",
            requestUri.AbsoluteUri);
        Assert.AreEqual("Bearer", authorization?.Scheme);
        Assert.AreEqual("test-token", authorization?.Parameter);
        Assert.AreEqual("downloaded", result.Status);
        Assert.AreEqual(appId, result.AppId);
        Assert.AreEqual("My App", result.Name);
        Assert.AreEqual("Test Publisher", result.Publisher);
        Assert.AreEqual("2.1.0.0", result.Version);
        Assert.AreEqual(Path.Combine(_root, "Test Publisher_My App_2.1.0.0.app"), result.FilePath);
        Assert.IsTrue(File.Exists(result.FilePath));
        Assert.AreEqual(packageBytes.LongLength, result.SizeBytes);
        Assert.HasCount(1, result.Dependencies);
        Assert.AreEqual(dependencyId, result.Dependencies[0].AppId);
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task DownloadAsync_IdenticalPackage_ReturnsAlreadyPresent()
    {
        var appId = Guid.NewGuid();
        var packageBytes = CreatePackageBytes(appId, "My App", new Version(1, 0, 0, 0));
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(packageBytes)
            }));
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);

        _ = await service.DownloadAsync(
            appId,
            "Sandbox",
            _root,
            force: false,
            _testContext.CancellationToken);
        var result = await service.DownloadAsync(
            appId,
            "Sandbox",
            _root,
            force: false,
            _testContext.CancellationToken);

        Assert.AreEqual("alreadyPresent", result.Status);
        Assert.IsEmpty(Directory.GetFiles(_root, ".bc-symbol-*.tmp"));
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task DownloadManyAsync_MultipleApps_DownloadsInParallelAndPreservesOrder()
    {
        var appIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var packages = appIds.ToDictionary(
            appId => appId,
            appId => CreatePackageBytes(appId, $"App {appId:N}", new Version(1, 0, 0, 0)));
        var activeDownloads = 0;
        var maximumActiveDownloads = 0;
        var concurrencyLock = new object();
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var appId = appIds.Single(id => request.RequestUri!.Query.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase));
            var active = Interlocked.Increment(ref activeDownloads);
            lock (concurrencyLock)
            {
                maximumActiveDownloads = Math.Max(maximumActiveDownloads, active);
            }

            try
            {
                await Task.Delay(100, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(packages[appId])
                };
            }
            finally
            {
                Interlocked.Decrement(ref activeDownloads);
            }
        });
        using var httpClient = CreateHttpClient(handler);

        var result = await CreateService(httpClient).DownloadManyAsync(
            appIds,
            "Sandbox",
            _root,
            force: false,
            maxParallelDownloads: 2,
            _testContext.CancellationToken);

        Assert.AreEqual("succeeded", result.Status);
        Assert.HasCount(appIds.Length, result.Apps);
        for (var index = 0; index < appIds.Length; index++)
        {
            Assert.AreEqual(appIds[index], result.Apps[index].AppId);
            Assert.AreEqual("downloaded", result.Apps[index].Status);
            Assert.IsNotNull(result.Apps[index].Symbol);
        }

        Assert.IsGreaterThan(1, maximumActiveDownloads);
        Assert.IsLessThanOrEqualTo(2, maximumActiveDownloads);
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task DownloadManyAsync_OneRequestFails_ReturnsPartialResults()
    {
        var successfulAppId = Guid.NewGuid();
        var failedAppId = Guid.NewGuid();
        var packageBytes = CreatePackageBytes(
            successfulAppId,
            "Successful App",
            new Version(1, 0, 0, 0));
        using var handler = new StubHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.Query.Contains(successfulAppId.ToString("D"), StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(packageBytes)
                }
                : new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("app unavailable")
                }));
        using var httpClient = CreateHttpClient(handler);

        var result = await CreateService(httpClient).DownloadManyAsync(
            [successfulAppId, failedAppId],
            "Sandbox",
            _root,
            force: false,
            maxParallelDownloads: 2,
            _testContext.CancellationToken);

        Assert.AreEqual("partial", result.Status);
        Assert.HasCount(2, result.Apps);
        Assert.AreEqual("downloaded", result.Apps[0].Status);
        Assert.IsNotNull(result.Apps[0].Symbol);
        Assert.AreEqual("failed", result.Apps[1].Status);
        Assert.IsNull(result.Apps[1].Symbol);
        Assert.IsNotNull(result.Apps[1].Message);
        Assert.Contains("400", result.Apps[1].Message!);
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task DownloadManyAsync_InvalidAppIds_RejectsBeforeRequest()
    {
        var requestSent = false;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            requestSent = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);
        var duplicateAppId = Guid.NewGuid();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.DownloadManyAsync(
            [],
            "Sandbox",
            _root,
            force: false,
            maxParallelDownloads: 2,
            _testContext.CancellationToken));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.DownloadManyAsync(
            [duplicateAppId, duplicateAppId],
            "Sandbox",
            _root,
            force: false,
            maxParallelDownloads: 2,
            _testContext.CancellationToken));

        Assert.IsFalse(requestSent);
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task DownloadManyAsync_ConcurrencyAboveFour_RejectsBeforeRequest()
    {
        var requestSent = false;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            requestSent = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var httpClient = CreateHttpClient(handler);

        var exception = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            CreateService(httpClient).DownloadManyAsync(
                [Guid.NewGuid()],
                "Sandbox",
                _root,
                force: false,
                maxParallelDownloads: 5,
                _testContext.CancellationToken));

        Assert.Contains("between 1 and 4", exception.Message);
        Assert.IsFalse(requestSent);
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task DownloadAsync_DifferentSameNamePackage_RequiresForceToReplace()
    {
        var appId = Guid.NewGuid();
        var firstPackage = CreatePackageBytes(appId, "My App", new Version(1, 0, 0, 0));
        var secondPackage = CreatePackageBytes(appId, "My App", new Version(1, 0, 0, 0));
        var responses = new Queue<byte[]>([firstPackage, secondPackage, secondPackage]);
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responses.Dequeue())
            }));
        using var httpClient = CreateHttpClient(handler);
        var service = CreateService(httpClient);
        var firstResult = await service.DownloadAsync(
            appId,
            "Sandbox",
            _root,
            force: false,
            _testContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<IOException>(() => service.DownloadAsync(
            appId,
            "Sandbox",
            _root,
            force: false,
            _testContext.CancellationToken));
        var replacedResult = await service.DownloadAsync(
            appId,
            "Sandbox",
            _root,
            force: true,
            _testContext.CancellationToken);

        Assert.AreEqual("replaced", replacedResult.Status);
        Assert.AreNotEqual(firstResult.Sha256, replacedResult.Sha256);
        Assert.IsEmpty(Directory.GetFiles(_root, ".bc-symbol-*.tmp"));
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task DownloadAsync_MismatchedAppId_RejectsAndDeletesTemporaryFile()
    {
        var requestedAppId = Guid.NewGuid();
        var packageBytes = CreatePackageBytes(
            Guid.NewGuid(),
            "Wrong App",
            new Version(1, 0, 0, 0));
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(packageBytes)
            }));
        using var httpClient = CreateHttpClient(handler);

        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            CreateService(httpClient).DownloadAsync(
                requestedAppId,
                "Production",
                _root,
                force: false,
                _testContext.CancellationToken));

        Assert.Contains(requestedAppId.ToString("D"), exception.Message);
        Assert.IsEmpty(Directory.GetFiles(_root));
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task DownloadAsync_DirectoryOutsideAllowedRoot_RejectsBeforeRequest()
    {
        var requestSent = false;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            requestSent = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var httpClient = CreateHttpClient(handler);
        var outsideDirectory = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
            CreateService(httpClient).DownloadAsync(
                Guid.NewGuid(),
                "Sandbox",
                outsideDirectory,
                force: false,
                _testContext.CancellationToken));

        Assert.IsFalse(requestSent);
        Assert.IsFalse(Directory.Exists(outsideDirectory));
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task DownloadAsync_MissingSymbolDirectory_RejectsBeforeRequest()
    {
        var requestSent = false;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            requestSent = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var httpClient = CreateHttpClient(handler);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            CreateService(httpClient).DownloadAsync(
                Guid.NewGuid(),
                "Sandbox",
                string.Empty,
                force: false,
                _testContext.CancellationToken));

        Assert.IsFalse(requestSent);
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task DownloadPackageAsync_ContentLengthAboveLimit_RejectsBeforeWriting()
    {
        const long maximumBytes = 150L * 1024 * 1024;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            var content = new ByteArrayContent([]);
            content.Headers.ContentLength = maximumBytes + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var httpClient = CreateHttpClient(handler);
        var destinationPath = Path.Combine(_root, "oversized.tmp");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            CreateDevClient(httpClient).DownloadPackageAsync(
                Guid.NewGuid(),
                "Sandbox",
                destinationPath,
                maximumBytes,
                _testContext.CancellationToken));

        Assert.IsFalse(File.Exists(destinationPath));
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private byte[] CreatePackageBytes(
        Guid appId,
        string name,
        Version version,
        params (Guid AppId, string Name, Version MinimumVersion)[] dependencies)
    {
        var path = Path.Combine(_root, $"fixture-{Guid.NewGuid():N}.app");
        TestPackageFactory.CreateApp(path, appId, name, version, dependencies);
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        return bytes;
    }

    private SymbolDownloadService CreateService(HttpClient httpClient)
    {
        var options = CreateOptions();
        return new SymbolDownloadService(
            new BusinessCentralDevClient(httpClient, new StubTokenCredential(), options),
            options);
    }

    private BusinessCentralDevClient CreateDevClient(HttpClient httpClient) =>
        new(httpClient, new StubTokenCredential(), CreateOptions());

    private AdminCenterOptions CreateOptions() => new()
    {
        AuthMode = "ClientSecret",
        TenantId = "test-tenant",
        AllowedAppRoots = [_root],
        AllowedSymbolRoots = [_root]
    };

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://api.businesscentral.dynamics.com/")
    };

    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessToken("test-token", DateTimeOffset.MaxValue));
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
