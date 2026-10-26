using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Azure.Core;
using ModelContextProtocol;

namespace BcDeployment.Tests;

[TestClass]
public sealed class DeploymentOrchestratorTests : IDisposable
{
    private readonly TestContext _testContext;
    private readonly string _root;
    private readonly string _journalDirectory;

    public DeploymentOrchestratorTests(TestContext testContext)
    {
        _testContext = testContext;
        _root = Path.Combine(Path.GetTempPath(), $"bc-orchestrator-tests-{Guid.NewGuid():N}");
        _journalDirectory = Path.Combine(_root, "journal");
        Directory.CreateDirectory(_root);
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task DeployAsync_TwoEnvironments_RunsInParallelAndKeepsPackageOrder()
    {
        var bundle = CreateBundle();
        var requests = new ConcurrentQueue<(string Environment, string FileName)>();
        var dependencyStarts = 0;
        var bothDependenciesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (IsEnvironmentListRequest(request))
            {
                return EnvironmentsResponse(("Sandbox East", "Sandbox"), ("Sandbox West", "Sandbox"));
            }

            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse("{\"value\":[]}");
            }

            var environment = EnvironmentFrom(request);
            var fileName = FileNameFrom(request);
            requests.Enqueue((environment, fileName));
            if (fileName == "Dependency.app")
            {
                if (Interlocked.Increment(ref dependencyStarts) == 2)
                {
                    bothDependenciesStarted.SetResult();
                }

                await bothDependenciesStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            }

            var appId = fileName == "Dependency.app" ? bundle.DependencyId : bundle.MainId;
            return JsonResponse($$"""
                {
                  "id": "{{Guid.NewGuid()}}",
                  "appId": "{{appId}}",
                  "status": "succeeded",
                  "targetAppVersion": "1.0.0.0"
                }
                """);
        });
        using var httpClient = CreateHttpClient(handler);
        var orchestrator = CreateOrchestrator(httpClient);

        var result = await orchestrator.DeployAsync(
            bundle.ZipPath,
            ["Sandbox East", "Sandbox West"],
            DeploymentSchedule.Immediate,
            SyncMode.Add,
            "en-US",
            true,
            false,
            30,
            2,
            NullProgress.Instance,
            _testContext.CancellationToken);

        Assert.AreEqual("succeeded", result.Status);
        Assert.HasCount(2, result.Environments);
        Assert.AreEqual(2, dependencyStarts);
        foreach (var environment in result.Environments)
        {
            CollectionAssert.AreEqual(
                new[] { bundle.DependencyId, bundle.MainId },
                environment.Apps.Select(app => app.AppId).ToArray());
        }

        foreach (var environment in new[] { "Sandbox East", "Sandbox West" })
        {
            CollectionAssert.AreEqual(
                new[] { "Dependency.app", "Main.app" },
                requests.Where(item => item.Environment == environment).Select(item => item.FileName).ToArray());
        }

        var journalPath = Path.Combine(_journalDirectory, $"{result.DeploymentId:D}.jsonl");
        Assert.IsTrue(File.Exists(journalPath));
        var journal = await File.ReadAllTextAsync(journalPath, _testContext.CancellationToken);
        StringAssert.Contains(journal, "operationAccepted");
        StringAssert.Contains(journal, "completed");
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task DeployAsync_ScheduledOperation_ReturnsScheduleKind()
    {
        var appId = Guid.NewGuid();
        var appPath = Path.Combine(_root, "Schedule.app");
        TestPackageFactory.CreateApp(appPath, appId, "Schedule", new Version(1, 0, 0, 0));
        string? submittedSchedule = null;
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (IsEnvironmentListRequest(request))
            {
                return EnvironmentsResponse(("Sandbox", "Sandbox"));
            }

            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse("{\"value\":[]}");
            }

            var form = Assert.IsInstanceOfType<MultipartFormDataContent>(request.Content);
            var schedulePart = form.Single(part =>
                part.Headers.ContentDisposition?.Name?.Trim('"') == "deploymentSchedule");
            submittedSchedule = await schedulePart.ReadAsStringAsync(cancellationToken);
            return JsonResponse($$"""
                {
                  "id": "{{Guid.NewGuid()}}",
                  "appId": "{{appId}}",
                  "status": "scheduled",
                  "scheduleKind": "UpdateWindow"
                }
                """);
        });
        using var httpClient = CreateHttpClient(handler);

        var result = await CreateOrchestrator(httpClient).DeployAsync(
            appPath,
            ["Sandbox"],
            DeploymentSchedule.UpdateWindow,
            SyncMode.Add,
            "en-US",
            true,
            false,
            30,
            2,
            NullProgress.Instance,
            _testContext.CancellationToken);

        Assert.AreEqual("UpdateWindow", submittedSchedule);
        Assert.AreEqual("scheduled", result.Status);
        Assert.AreEqual("UpdateWindow", result.Environments[0].Apps[0].ScheduleKind);
    }

    [TestMethod]
    [Timeout(5000)]
    [DataRow(DeploymentSchedule.UpdateWindow)]
    [DataRow(DeploymentSchedule.NextMinorUpdate)]
    [DataRow(DeploymentSchedule.NextMajorUpdate)]
    public async Task DeployAsync_ScheduledBundle_SubmitsAndJournalsInDependencyOrder(
        DeploymentSchedule deploymentSchedule)
    {
        var bundle = CreateBundle();
        var requests = new List<(string FileName, string Schedule)>();
        using var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            if (IsEnvironmentListRequest(request))
            {
                return EnvironmentsResponse(("Sandbox", "Sandbox"));
            }

            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse("{\"value\":[]}");
            }

            if (requests.Count > 0)
            {
                var journalFiles = Directory.GetFiles(_journalDirectory, "*.jsonl");
                Assert.HasCount(1, journalFiles);
                var journal = await File.ReadAllTextAsync(journalFiles[0], cancellationToken);
                StringAssert.Contains(journal, "operationAccepted");
                StringAssert.Contains(journal, bundle.DependencyId.ToString());
            }

            var fileName = FileNameFrom(request);
            var form = Assert.IsInstanceOfType<MultipartFormDataContent>(request.Content);
            var schedulePart = form.Single(part =>
                part.Headers.ContentDisposition?.Name?.Trim('"') == "deploymentSchedule");
            var submittedSchedule = await schedulePart.ReadAsStringAsync(cancellationToken);
            requests.Add((fileName, submittedSchedule));
            var appId = fileName == "Dependency.app" ? bundle.DependencyId : bundle.MainId;
            return JsonResponse($$"""
                {
                  "id": "{{Guid.NewGuid()}}",
                  "appId": "{{appId}}",
                  "status": "scheduled",
                  "scheduleKind": "{{deploymentSchedule}}",
                  "targetAppVersion": "1.0.0.0"
                }
                """);
        });
        using var httpClient = CreateHttpClient(handler);

        var result = await CreateOrchestrator(httpClient).DeployAsync(
            bundle.ZipPath,
            ["Sandbox"],
            deploymentSchedule,
            SyncMode.Add,
            "en-US",
            true,
            false,
            30,
            2,
            NullProgress.Instance,
            _testContext.CancellationToken);

        Assert.AreEqual("scheduled", result.Status);
        CollectionAssert.AreEqual(
            new[] { "Dependency.app", "Main.app" },
            requests.Select(request => request.FileName).ToArray());
        Assert.IsTrue(requests.All(request => request.Schedule == deploymentSchedule.ToString()));
        CollectionAssert.AreEqual(
            new[] { bundle.DependencyId, bundle.MainId },
            result.PackageOrder.Select(package => package.AppId).ToArray());
        Assert.IsTrue(result.Environments[0].Apps.All(app => app.Status == "scheduled"));
        Assert.IsTrue(result.Environments[0].Apps.All(app => app.ScheduleKind == deploymentSchedule.ToString()));
        Assert.IsTrue(result.Environments[0].Apps.All(app => app.OperationId.HasValue));
        Assert.IsTrue(result.Environments[0].Apps.All(app => app.Operation?.TargetVersion == "1.0.0.0"));

        var journalPath = Path.Combine(_journalDirectory, $"{result.DeploymentId:D}.jsonl");
        var completedJournal = await File.ReadAllTextAsync(journalPath, _testContext.CancellationToken);
        Assert.AreEqual(2, completedJournal.Split("operationAccepted").Length - 1);
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task DeployAsync_ScheduledBundlePrerequisiteSubmissionFails_BlocksDependentPackage()
    {
        var bundle = CreateBundle();
        var uploadCount = 0;
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (IsEnvironmentListRequest(request))
            {
                return Task.FromResult(EnvironmentsResponse(("Sandbox", "Sandbox")));
            }

            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(JsonResponse("{\"value\":[]}"));
            }

            uploadCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("Prerequisite submission failed.", Encoding.UTF8, "text/plain")
            });
        });
        using var httpClient = CreateHttpClient(handler);

        var result = await CreateOrchestrator(httpClient).DeployAsync(
            bundle.ZipPath,
            ["Sandbox"],
            DeploymentSchedule.NextMajorUpdate,
            SyncMode.Add,
            "en-US",
            true,
            false,
            30,
            2,
            NullProgress.Instance,
            _testContext.CancellationToken);

        Assert.AreEqual("failed", result.Status);
        Assert.AreEqual(1, uploadCount);
        Assert.AreEqual("failed", result.Environments[0].Apps[0].Status);
        Assert.AreEqual("blocked", result.Environments[0].Apps[1].Status);
        StringAssert.Contains(result.Environments[0].Apps[0].Message, "Prerequisite submission failed.");
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task DeployAsync_RunningOperation_ReportsLiveStatusWhilePolling()
    {
        var appId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var completedOn = new DateTimeOffset(2026, 9, 10, 8, 13, 45, TimeSpan.Zero);
        var appPath = Path.Combine(_root, "Progress.app");
        TestPackageFactory.CreateApp(appPath, appId, "Progress", new Version(1, 0, 0, 0));
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (IsEnvironmentListRequest(request))
            {
                return Task.FromResult(EnvironmentsResponse(("Sandbox", "Sandbox")));
            }

            if (request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath.EndsWith("/apps", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("{\"value\":[]}"));
            }

            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(JsonResponse($$"""
                                        {
                                            "id": "{{operationId}}",
                                            "appId": "{{appId}}",
                                            "status": "running",
                                            "targetAppVersion": "1.0.0.0"
                                        }
                                        """));
            }

            return Task.FromResult(JsonResponse($$"""
                {
                  "id": "{{operationId}}",
                  "appId": "{{appId}}",
                                    "status": "succeeded",
                                    "type": "install",
                                    "sourceVersion": "0.9.0.0",
                                    "targetAppVersion": "1.0.0.0",
                                    "createdOn": "2026-09-10T08:13:20+00:00",
                                    "startedOn": "2026-09-10T08:13:25+00:00",
                                    "completedOn": "{{completedOn:O}}",
                                    "scheduleKind": "Immediate"
                }
                """));
        });
        using var httpClient = CreateHttpClient(handler);
        var progress = new RecordingProgress();

        var result = await CreateOrchestrator(httpClient).DeployAsync(
            appPath,
            ["Sandbox"],
            DeploymentSchedule.Immediate,
            SyncMode.Add,
            "en-US",
            true,
            false,
            30,
            2,
            progress,
            _testContext.CancellationToken);

        Assert.AreEqual("succeeded", result.Status);
        var appResult = result.Environments[0].Apps[0];
        Assert.IsNotNull(appResult.Operation);
        Assert.AreEqual(operationId, appResult.Operation.OperationId);
        Assert.AreEqual("install", appResult.Operation.Type);
        Assert.AreEqual("0.9.0.0", appResult.Operation.SourceVersion);
        Assert.AreEqual("1.0.0.0", appResult.Operation.TargetVersion);
        Assert.AreEqual(completedOn, appResult.Operation.CompletedOn);
        Assert.IsTrue(progress.Messages.Any(message => message.Contains("checking installed app versions", StringComparison.Ordinal)));
        Assert.IsTrue(progress.Messages.Any(message => message.Contains("uploading Progress 1.0.0.0", StringComparison.Ordinal)));
        Assert.IsTrue(progress.Messages.Any(message => message.Contains($"operation {operationId:D} accepted", StringComparison.Ordinal)));
        Assert.IsTrue(progress.Messages.Any(message => message.Contains("is 'succeeded' after", StringComparison.Ordinal)));
        Assert.IsTrue(progress.Messages.Any(message => message.Contains("Completed 1 of 1", StringComparison.Ordinal)));
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task DeployAsync_DependencyFails_BlocksRemainingPackage()
    {
        var bundle = CreateBundle();
        var uploadCount = 0;
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (IsEnvironmentListRequest(request))
            {
                return Task.FromResult(EnvironmentsResponse(("Sandbox", "Sandbox")));
            }

            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(JsonResponse("{\"value\":[]}"));
            }

            uploadCount++;
            return Task.FromResult(JsonResponse($$"""
                {
                  "id": "{{Guid.NewGuid()}}",
                  "appId": "{{bundle.DependencyId}}",
                  "status": "failed",
                  "errorMessage": "Dependency installation failed."
                }
                """));
        });
        using var httpClient = CreateHttpClient(handler);

        var result = await CreateOrchestrator(httpClient).DeployAsync(
            bundle.ZipPath,
            ["Sandbox"],
            DeploymentSchedule.Immediate,
            SyncMode.Add,
            "en-US",
            true,
            false,
            30,
            2,
            NullProgress.Instance,
            _testContext.CancellationToken);

        Assert.AreEqual("failed", result.Status);
        Assert.AreEqual(1, uploadCount);
        Assert.HasCount(2, result.Environments[0].Apps);
        Assert.AreEqual("failed", result.Environments[0].Apps[0].Status);
        Assert.AreEqual("blocked", result.Environments[0].Apps[1].Status);
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task DeployAsync_ProductionEnvironmentNotAllowed_RejectsBeforeReadingPackageOrUploading()
    {
        var requestCount = 0;
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            requestCount++;
            Assert.IsTrue(IsEnvironmentListRequest(request));
            return Task.FromResult(EnvironmentsResponse(("Live", "Production")));
        });
        using var httpClient = CreateHttpClient(handler);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            CreateOrchestrator(httpClient).DeployAsync(
                Path.Combine(_root, "missing.app"),
                ["Live"],
                DeploymentSchedule.Immediate,
                SyncMode.Add,
                "en-US",
                true,
                true,
                30,
                2,
                NullProgress.Instance,
                _testContext.CancellationToken));

        StringAssert.Contains(exception.Message, "BC_ALLOW_PRODUCTION_DEPLOYMENT");
        Assert.AreEqual(1, requestCount);
        Assert.IsFalse(Directory.Exists(_journalDirectory));
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task DeployAsync_ProductionAllowedButNotConfirmed_RejectsBeforeReadingPackageOrUploading()
    {
        var requestCount = 0;
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            requestCount++;
            Assert.IsTrue(IsEnvironmentListRequest(request));
            return Task.FromResult(EnvironmentsResponse(("Live", "Production")));
        });
        using var httpClient = CreateHttpClient(handler);

        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            CreateOrchestrator(httpClient, allowProductionDeployment: true).DeployAsync(
                Path.Combine(_root, "missing.app"),
                ["Live"],
                DeploymentSchedule.Immediate,
                SyncMode.Add,
                "en-US",
                true,
                false,
                30,
                2,
                NullProgress.Instance,
                _testContext.CancellationToken));

        StringAssert.Contains(exception.Message, "confirmProduction");
        Assert.AreEqual(1, requestCount);
        Assert.IsFalse(Directory.Exists(_journalDirectory));
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task DeployAsync_ProductionAllowedAndConfirmed_Deploys()
    {
        var appId = Guid.NewGuid();
        var appPath = Path.Combine(_root, "Prod.app");
        TestPackageFactory.CreateApp(appPath, appId, "Prod", new Version(1, 0, 0, 0));
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (IsEnvironmentListRequest(request))
            {
                return Task.FromResult(EnvironmentsResponse(("Live", "Production")));
            }

            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(JsonResponse("{\"value\":[]}"));
            }

            return Task.FromResult(JsonResponse($$"""
                {
                  "id": "{{Guid.NewGuid()}}",
                  "appId": "{{appId}}",
                  "status": "succeeded",
                  "targetAppVersion": "1.0.0.0"
                }
                """));
        });
        using var httpClient = CreateHttpClient(handler);

        var result = await CreateOrchestrator(httpClient, allowProductionDeployment: true).DeployAsync(
            appPath,
            ["Live"],
            DeploymentSchedule.Immediate,
            SyncMode.Add,
            "en-US",
            true,
            true,
            30,
            2,
            NullProgress.Instance,
            _testContext.CancellationToken);

        Assert.AreEqual("succeeded", result.Status);
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private (string ZipPath, Guid DependencyId, Guid MainId) CreateBundle()
    {
        var dependencyId = Guid.NewGuid();
        var mainId = Guid.NewGuid();
        var dependencyPath = Path.Combine(_root, $"Dependency-{dependencyId:N}.app");
        var mainPath = Path.Combine(_root, $"Main-{mainId:N}.app");
        var zipPath = Path.Combine(_root, $"bundle-{mainId:N}.zip");
        TestPackageFactory.CreateApp(dependencyPath, dependencyId, "Dependency", new Version(1, 0, 0, 0));
        TestPackageFactory.CreateApp(
            mainPath,
            mainId,
            "Main",
            new Version(1, 0, 0, 0),
            (dependencyId, "Dependency", new Version(1, 0, 0, 0)));
        TestPackageFactory.CreateZip(zipPath, ("Main.app", mainPath), ("Dependency.app", dependencyPath));
        return (zipPath, dependencyId, mainId);
    }

    private DeploymentOrchestrator CreateOrchestrator(
        HttpClient httpClient,
        bool allowProductionDeployment = false)
    {
        var options = new AdminCenterOptions
        {
            AuthMode = "ClientSecret",
            AllowedAppRoots = [_root],
            AllowProductionDeployment = allowProductionDeployment,
            JournalDirectory = _journalDirectory
        };
        return new DeploymentOrchestrator(
            options,
            new PackageBundleReader(options),
            new BusinessCentralAdminClient(httpClient, new StubTokenCredential(), options),
            new DeploymentJournal(options));
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://api.businesscentral.dynamics.com/")
    };

    private static bool IsEnvironmentListRequest(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get
        && request.RequestUri!.AbsolutePath.EndsWith("/environments", StringComparison.Ordinal);

    private static HttpResponseMessage EnvironmentsResponse(params (string Name, string Type)[] environments)
    {
        var entries = string.Join(",", environments.Select(environment => $$"""
            {
              "name": "{{environment.Name}}",
              "friendlyName": "{{environment.Name}}",
              "type": "{{environment.Type}}",
              "countryCode": "US",
              "status": "Active",
              "versionDetails": { "version": "26.0" }
            }
            """));
        return JsonResponse($$"""{ "value": [{{entries}}] }""");
    }

    private static string EnvironmentFrom(HttpRequestMessage request)
    {
        var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.IndexOf(segments, "environments");
        return Uri.UnescapeDataString(segments[index + 1]);
    }

    private static string FileNameFrom(HttpRequestMessage request)
    {
        var multipart = Assert.IsInstanceOfType<MultipartFormDataContent>(request.Content);
        var file = multipart.Single(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "extensionFile");
        return file.Headers.ContentDisposition!.FileName!.Trim('"');
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
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

    private sealed class NullProgress : IProgress<ProgressNotificationValue>
    {
        public static NullProgress Instance { get; } = new();

        public void Report(ProgressNotificationValue value)
        {
        }
    }

    private sealed class RecordingProgress : IProgress<ProgressNotificationValue>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public void Report(ProgressNotificationValue value)
        {
            if (value.Message is not null)
            {
                Messages.Enqueue(value.Message);
            }
        }
    }
}
