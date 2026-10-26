using Azure.Core;
using BcDeployment;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
    options.LogToStandardErrorThreshold = LogLevel.Trace);

var adminCenterOptions = AdminCenterOptions.FromEnvironment();
builder.Services.AddSingleton(adminCenterOptions);
builder.Services.AddSingleton<TokenCredential>(_ => CredentialFactory.Create(adminCenterOptions));
builder.Services.AddSingleton(_ => new HttpClient
{
    BaseAddress = adminCenterOptions.BaseUri,
    Timeout = TimeSpan.FromMinutes(5)
});
builder.Services.AddSingleton<BusinessCentralAdminClient>();
builder.Services.AddSingleton<PackageBundleReader>();
builder.Services.AddSingleton<DeploymentJournal>();
builder.Services.AddSingleton<DeploymentOrchestrator>();
builder.Services.AddSingleton<AppDetailsService>();
builder.Services.AddSingleton<BusinessCentralDevClient>();
builder.Services.AddSingleton<SymbolDownloadService>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<BcDeploymentTools>();

await builder.Build().RunAsync();
