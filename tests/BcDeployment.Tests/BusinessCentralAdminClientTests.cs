using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;

namespace BcDeployment.Tests;

[TestClass]
public sealed class BusinessCentralAdminClientTests : IDisposable
{
    private readonly TestContext _testContext;
    private readonly string _allowedRoot;

    public BusinessCentralAdminClientTests(TestContext testContext)
    {
        _testContext = testContext;
        _allowedRoot = Path.Combine(Path.GetTempPath(), $"bc-deployment-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_allowedRoot);
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task PublishPteAsync_ValidPackage_SendsDocumentedMultipartRequest()
    {
        var appId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var appPath = Path.Combine(_allowedRoot, "My App.app");
        await File.WriteAllBytesAsync(appPath, [1, 2, 3], _testContext.CancellationToken);
        RequestSnapshot? snapshot = null;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            snapshot = await RequestSnapshot.CreateAsync(request, cancellationToken);
            return JsonResponse($$"""
                {
                  "id": "{{operationId}}",
                  "appId": "{{appId}}",
                  "type": "install",
                  "status": "running",
                  "targetAppVersion": "2.0.0.0",
                  "scheduleKind": "Immediate"
                }
                """);
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.businesscentral.dynamics.com/")
        };
        var client = CreateClient(httpClient);

        var result = await client.PublishPteAsync(
            appPath,
            "Sandbox West",
            DeploymentSchedule.Immediate,
            SyncMode.Add,
            "en-US",
            true,
            _testContext.CancellationToken);

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(HttpMethod.Post, snapshot.Method);
        Assert.AreEqual(
            "https://api.businesscentral.dynamics.com/admin/v2.29/applications/BusinessCentral/environments/Sandbox%20West/apps/pteInstall",
            snapshot.Uri.AbsoluteUri);
        Assert.AreEqual("Bearer", snapshot.Authorization?.Scheme);
        Assert.AreEqual("test-token", snapshot.Authorization?.Parameter);
        Assert.AreEqual("My App.app", snapshot.FileName);
        Assert.AreEqual("Immediate", snapshot.Fields["deploymentSchedule"]);
        Assert.AreEqual("Add", snapshot.Fields["syncMode"]);
        Assert.AreEqual("en-US", snapshot.Fields["languageId"]);
        Assert.AreEqual("true", snapshot.Fields["acceptIsvEula"]);
        Assert.AreEqual("true", snapshot.Fields["installOrUpdateNeededDependencies"]);
        Assert.AreEqual(operationId, result.OperationId);
        Assert.AreEqual(appId, result.AppId);
        Assert.AreEqual("running", result.Status);
        Assert.AreEqual("2.0.0.0", result.TargetVersion);
        Assert.AreEqual("Immediate", result.ScheduleKind);
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task GetOperationAsync_WrappedResponse_NormalizesOperation()
    {
        var appId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var createdOn = new DateTimeOffset(2026, 9, 10, 8, 12, 30, TimeSpan.Zero);
        var startedOn = createdOn.AddSeconds(4);
        var completedOn = startedOn.AddSeconds(16);
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.AreEqual(
                $"https://api.businesscentral.dynamics.com/admin/v2.29/applications/BusinessCentral/environments/Production/apps/{appId:D}/operations/{operationId:D}",
                request.RequestUri?.AbsoluteUri);
            return Task.FromResult(JsonResponse($$"""
                {
                  "value": [
                    {
                      "id": "{{operationId}}",
                      "type": "Install",
                      "status": "Succeeded",
                      "sourceVersion": "1.0.0.0",
                      "targetVersion": "2.0.0.0",
                      "createdOn": "{{createdOn:O}}",
                      "startedOn": "{{startedOn:O}}",
                      "completedOn": "{{completedOn:O}}",
                      "errorMessage": "",
                      "scheduleKind": "Immediate"
                    }
                  ]
                }
                """));
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.businesscentral.dynamics.com/")
        };
        var client = CreateClient(httpClient);

        var result = await client.GetOperationAsync(
            "Production",
            appId,
            operationId,
            _testContext.CancellationToken);

        Assert.AreEqual(operationId, result.OperationId);
        Assert.AreEqual(appId, result.AppId);
        Assert.AreEqual("succeeded", result.Status);
        Assert.AreEqual("1.0.0.0", result.SourceVersion);
        Assert.AreEqual("2.0.0.0", result.TargetVersion);
        Assert.AreEqual(createdOn, result.CreatedOn);
        Assert.AreEqual(startedOn, result.StartedOn);
        Assert.AreEqual(completedOn, result.CompletedOn);
        Assert.IsNull(result.ErrorMessage);
        Assert.AreEqual("Immediate", result.ScheduleKind);
        Assert.IsTrue(result.IsTerminal);
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task GetInstalledAppsAsync_LiveResponse_MapsInstalledVersion()
    {
        var appId = Guid.NewGuid();
        var lastUpdateOperationId = Guid.NewGuid();
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse($$"""
            {
              "value": [
                {
                  "id": "{{appId}}",
                  "name": "My App",
                  "publisher": "Test Publisher",
                  "version": "3.2.1.0",
                                    "state": "Installed",
                                    "lastUpdateOperationId": "{{lastUpdateOperationId}}"
                }
              ]
            }
            """)));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.businesscentral.dynamics.com/")
        };

        var result = await CreateClient(httpClient).GetInstalledAppsAsync(
            "Production",
            _testContext.CancellationToken);

        Assert.HasCount(1, result);
        Assert.AreEqual(appId, result[0].AppId);
        Assert.AreEqual(new Version(3, 2, 1, 0), result[0].Version);
        Assert.AreEqual("Installed", result[0].State);
        Assert.AreEqual(lastUpdateOperationId, result[0].LastUpdateOperationId);
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task GetEnvironmentsAsync_DocumentedResponse_MapsEnvironmentDetails()
    {
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.AreEqual(
                            "https://api.businesscentral.dynamics.com/admin/v2.29/applications/BusinessCentral/environments",
                            request.RequestUri?.AbsoluteUri);
            return Task.FromResult(JsonResponse("""
                                {
                                    "value": [
                                        {
                                            "name": "Sandbox West",
                                            "friendlyName": "West testing",
                                            "type": "Sandbox",
                                            "countryCode": "GB",
                                            "status": "Active",
                                            "versionDetails": { "version": "26.3" }
                                        }
                                    ]
                                }
                                """));
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.businesscentral.dynamics.com/")
        };

        var result = await CreateClient(httpClient).GetEnvironmentsAsync(_testContext.CancellationToken);

        Assert.HasCount(1, result);
        Assert.AreEqual("Sandbox West", result[0].Name);
        Assert.AreEqual("West testing", result[0].FriendlyName);
        Assert.AreEqual("Sandbox", result[0].Type);
        Assert.AreEqual("GB", result[0].CountryCode);
        Assert.AreEqual("Active", result[0].Status);
        Assert.AreEqual("26.3", result[0].ApplicationVersion);
    }

    [TestMethod]
    public void BusinessCentralEnvironmentSerialization_OmitsFriendlyName()
    {
        var environment = new BusinessCentralEnvironment(
            "BC-NL-DEV",
            "accountsfreighthub-BC-NL-DEV",
            "Sandbox",
            "NL",
            "Active",
            "28.0");

        var json = JsonSerializer.Serialize(environment, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.IsFalse(json.Contains("friendlyName", StringComparison.Ordinal));
        StringAssert.Contains(json, "\"countryCode\":\"NL\"");
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task AppDetailsService_AppInstalledInEnvironment_ReturnsCurrentDetails()
    {
        var appId = Guid.NewGuid();
        var lastUpdateOperationId = Guid.NewGuid();
        var requestedPaths = new List<string>();
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/environments/Sandbox", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""
                                        {
                                            "name": "Sandbox",
                                            "friendlyName": "Test",
                                            "type": "Sandbox",
                                            "countryCode": "GB",
                                            "status": "Active",
                                            "versionDetails": { "version": "26.3" }
                                        }
                                        """));
            }

            Assert.IsTrue(request.RequestUri.AbsolutePath.EndsWith("/environments/Sandbox/apps", StringComparison.Ordinal));
            return Task.FromResult(JsonResponse($$"""
                                {
                                    "value": [
                                        {
                                            "appId": "{{appId}}",
                                            "name": "My App",
                                            "publisher": "Test Publisher",
                                            "version": "2.1.0.0",
                                            "state": "Installed",
                                            "appType": "PTE",
                                            "canBeUninstalled": true,
                                            "lastUpdateOperationId": "{{lastUpdateOperationId}}",
                                            "lastUpdateAttemptResult": "Succeeded"
                                        }
                                    ]
                                }
                                """));
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.businesscentral.dynamics.com/")
        };

        var result = await new AppDetailsService(CreateClient(httpClient))
            .GetAsync(appId, "Sandbox", _testContext.CancellationToken);

        Assert.IsTrue(result.Found);
        Assert.AreEqual("My App", result.Name);
        Assert.AreEqual("Test Publisher", result.Publisher);
        Assert.HasCount(1, result.Installations);
        Assert.IsEmpty(result.LookupErrors);
        var sandbox = result.Installations[0];
        Assert.AreEqual("2.1.0.0", sandbox.AppVersion);
        Assert.AreEqual("Sandbox", sandbox.EnvironmentType);
        Assert.AreEqual("26.3", sandbox.EnvironmentVersion);
        Assert.AreEqual("PTE", sandbox.AppType);
        Assert.IsTrue(sandbox.CanBeUninstalled);
        Assert.AreEqual(lastUpdateOperationId, sandbox.LastUpdateOperationId);
        Assert.AreEqual("Succeeded", sandbox.LastUpdateAttemptResult);
        CollectionAssert.AreEqual(
            new[]
            {
                        "/admin/v2.29/applications/BusinessCentral/environments/Sandbox",
                        "/admin/v2.29/applications/BusinessCentral/environments/Sandbox/apps"
            },
            requestedPaths);
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task AppDetailsService_AppNotInstalled_ReturnsNotFound()
    {
        using var handler = new StubHttpMessageHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/environments/Sandbox", StringComparison.Ordinal)
                        ? JsonResponse("""
                                        {
                                            "value": [
                                                { "name": "Sandbox", "type": "Sandbox", "versionDetails": { "version": "26.3" } }
                                            ]
                                        }
                                        """)
                        : JsonResponse("{\"value\":[]}")));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.businesscentral.dynamics.com/")
        };

        var result = await new AppDetailsService(CreateClient(httpClient))
            .GetAsync(Guid.NewGuid(), "Sandbox", _testContext.CancellationToken);

        Assert.IsFalse(result.Found);
        Assert.IsNull(result.Name);
        Assert.IsEmpty(result.Installations);
        Assert.IsEmpty(result.LookupErrors);
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task GetOperationAsync_ServiceUnavailable_RetriesRequest()
    {
        var appId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var attempts = 0;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            attempts++;
            if (attempts == 1)
            {
                var transientResponse = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                transientResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(transientResponse);
            }

            return Task.FromResult(JsonResponse($$"""
                {
                  "id": "{{operationId}}",
                  "appId": "{{appId}}",
                  "status": "succeeded"
                }
                """));
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.businesscentral.dynamics.com/")
        };

        var result = await CreateClient(httpClient).GetOperationAsync(
            "Production",
            appId,
            operationId,
            _testContext.CancellationToken);

        Assert.AreEqual(2, attempts);
        Assert.AreEqual("succeeded", result.Status);
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task GetOperationAsync_BadRequest_DoesNotRetry()
    {
        var attempts = 0;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("invalid request")
            });
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.businesscentral.dynamics.com/")
        };

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            CreateClient(httpClient).GetOperationAsync(
                "Production",
                Guid.NewGuid(),
                Guid.NewGuid(),
                _testContext.CancellationToken));

        Assert.AreEqual(1, attempts);
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task PublishPteAsync_PathOutsideAllowedRoot_RejectsPackage()
    {
        var outsidePath = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.app");
        await File.WriteAllBytesAsync(outsidePath, [1], _testContext.CancellationToken);
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            throw new AssertFailedException("HTTP must not be called for a rejected package.")))
        {
            BaseAddress = new Uri("https://api.businesscentral.dynamics.com/")
        };
        var client = CreateClient(httpClient);

        try
        {
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
                client.PublishPteAsync(
                    outsidePath,
                    "Sandbox",
                    DeploymentSchedule.Immediate,
                    SyncMode.Add,
                    "en-US",
                    false,
                    _testContext.CancellationToken));
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    public void Dispose()
    {
        Directory.Delete(_allowedRoot, true);
    }

    private BusinessCentralAdminClient CreateClient(HttpClient httpClient) =>
        new(
            httpClient,
            new StubTokenCredential(),
            new AdminCenterOptions
            {
                AuthMode = "ClientSecret",
                AllowedAppRoots = [_allowedRoot]
            });

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
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

    private sealed record RequestSnapshot(
        HttpMethod Method,
        Uri Uri,
        AuthenticationHeaderValue? Authorization,
        string? FileName,
        IReadOnlyDictionary<string, string> Fields)
    {
        public static async Task<RequestSnapshot> CreateAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = Assert.IsInstanceOfType<MultipartFormDataContent>(request.Content);
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            string? fileName = null;

            foreach (var part in content)
            {
                var disposition = part.Headers.ContentDisposition;
                var name = disposition?.Name?.Trim('"');
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (name == "extensionFile")
                {
                    fileName = disposition?.FileName?.Trim('"');
                }
                else
                {
                    fields[name] = await part.ReadAsStringAsync(cancellationToken);
                }
            }

            Assert.IsNotNull(request.RequestUri);
            return new RequestSnapshot(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization,
                fileName,
                fields);
        }
    }
}
