<!-- mcp-name: io.github.mundanebcdev/business-central-deployment-tool -->

# Business Central Deployment Tool

[![NuGet](https://img.shields.io/nuget/v/BusinessCentralDeploymentTool.svg?logo=nuget)](https://www.nuget.org/packages/BusinessCentralDeploymentTool)
[![Downloads](https://img.shields.io/nuget/dt/BusinessCentralDeploymentTool.svg?logo=nuget)](https://www.nuget.org/packages/BusinessCentralDeploymentTool)
[![CI](https://github.com/mundanebcdev/BusinessCentralDeploymentTool/actions/workflows/ci.yml/badge.svg)](https://github.com/mundanebcdev/BusinessCentralDeploymentTool/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/mundanebcdev/BusinessCentralDeploymentTool/blob/main/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download)

A local [Model Context Protocol](https://modelcontextprotocol.io) server that lets an
AI agent deploy Microsoft Dynamics 365 Business Central per-tenant extensions, inspect
installed apps, and download AL symbols through the Admin Center API v2.29.

Ask Copilot to *"deploy `MyExtension.zip` to the DE and NL sandboxes"* and get back one
combined result with per-environment status and Admin Center operation IDs.

## Tools

| Tool | Purpose |
| --- | --- |
| `bc_list_environments` | List tenant environments with type, country, status, and BC version |
| `bc_get_app_details` | Read the installed version and last update operation of one app in one environment |
| `bc_get_operation_details` | Get one Admin Center app operation with status, versions, timestamps, and error details |
| `bc_download_symbol` | Download installed app symbol packages into an allowlisted AL symbol cache, with at most 4 downloads at a time |
| `bc_deploy_pte` | Deploy one `.app` or a dependency-ordered ZIP bundle to many environments in parallel |

## What it does

- Accepts one `.app` file or a ZIP containing `.app` files.
- Validates NAVX packages and orders bundled apps by manifest dependency.
- Deploys apps sequentially inside each environment, and environments in parallel.
- Waits internally for immediate operations to finish, reporting progress as it goes.
- Submits deferred bundle operations in dependency order and returns after Admin Center accepts them.
- Skips an app when the same or a newer version is already installed.
- Returns partial results when environments differ, plus each complete Admin Center operation.
- Records accepted operation IDs in a local JSON Lines journal, so a cancelled call is
  still recoverable.
- Refuses Production environments unless the operator enables deployment and the caller authorizes the request.

The workflow is **not transactional**. If a later app fails, previously installed apps
are not rolled back.

## Prerequisites

1. [.NET SDK 10.0.400](https://dotnet.microsoft.com/download) or a compatible 10.0 patch.
2. A Microsoft Entra app registration.
3. The application permission `AdminCenter.ReadWrite.All` for Dynamics 365 Business
   Central, with admin consent granted.
4. The application added under **Authorized Microsoft Entra apps** in the Business
   Central admin center, for every customer tenant it manages.
5. A client secret or certificate for the service principal. Prefer a certificate.

Each customer tenant must independently authorize the application. Using a managed
identity does not remove that cross-tenant requirement.

## Install

The server ships as a .NET tool on NuGet.org. `dnx` downloads and runs it on demand, so
there is nothing to install up front.

### VS Code

Create `.vscode/mcp.json` in your AL workspace:

```json
{
  "servers": {
    "bc-deployment": {
      "type": "stdio",
      "command": "dnx",
      "args": ["BusinessCentralDeploymentTool", "--yes"],
      "env": {
        "BC_AUTH_MODE": "ClientSecret",
        "BC_TENANT_ID": "${input:bc-tenant-id}",
        "BC_CLIENT_ID": "${input:bc-client-id}",
        "BC_CLIENT_SECRET": "${input:bc-client-secret}",
        "BC_ALLOWED_APP_ROOTS": "${workspaceFolder}",
        "BC_ALLOWED_SYMBOL_ROOTS": "${workspaceFolder}/.alpackages",
        "BC_DEPLOYMENT_JOURNAL_DIRECTORY": "${workspaceFolder}/.bcdeployment/journal"
      }
    }
  },
  "inputs": [
    { "id": "bc-tenant-id", "type": "promptString", "description": "Microsoft Entra tenant ID" },
    { "id": "bc-client-id", "type": "promptString", "description": "Entra application client ID" },
    { "id": "bc-client-secret", "type": "promptString", "description": "Entra application client secret", "password": true }
  ]
}
```

Open the MCP Servers view, start `bc-deployment`, and enable its tools for Copilot.
VS Code prompts for the tenant ID, client ID, and secret. None of them are written to
the file.

### Other MCP clients

Any stdio MCP client works. Run `dnx BusinessCentralDeploymentTool --yes` with the
environment variables set. For example, in Claude Desktop's `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "bc-deployment": {
      "command": "dnx",
      "args": ["BusinessCentralDeploymentTool", "--yes"],
      "env": {
        "BC_AUTH_MODE": "ClientSecret",
        "BC_TENANT_ID": "...",
        "BC_CLIENT_ID": "...",
        "BC_CLIENT_SECRET": "...",
        "BC_ALLOWED_APP_ROOTS": "C:\\src\\MyExtension\\artifacts"
      }
    }
  }
}
```

Self-contained builds are attached to each
[GitHub release](https://github.com/mundanebcdev/BusinessCentralDeploymentTool/releases)
if you cannot use the .NET SDK.

### Teach your agent to use it well

Copy the snippet in
[docs/agent-instructions.md](https://github.com/mundanebcdev/BusinessCentralDeploymentTool/blob/main/docs/agent-instructions.md)
into your repository's agent instructions file.

## Configuration

All configuration is by environment variable.

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `BC_AUTH_MODE` | No | `ClientSecret`, or `Certificate` when a certificate path is set | `ClientSecret`, `Certificate`, or `ManagedIdentity` |
| `BC_TENANT_ID` | Yes | Not set | Tenant ID for secret or certificate authentication |
| `BC_CLIENT_ID` | Yes | Not set | App client ID, or the client ID of a user-assigned managed identity |
| `BC_CLIENT_SECRET` | Conditional | Not set | Client secret, for `ClientSecret` mode |
| `BC_CLIENT_CERTIFICATE_PATH` | Conditional | Not set | PFX path, for `Certificate` mode |
| `BC_CLIENT_CERTIFICATE_PASSWORD` | No | Not set | Optional PFX password |
| `BC_ALLOWED_APP_ROOTS` | Yes | Current directory | Directories the server may read packages from |
| `BC_ALLOWED_SYMBOL_ROOTS` | For symbols | empty | Directories the server may write symbols to |
| `BC_ALLOW_PRODUCTION_DEPLOYMENT` | No | `false` | Permits deployment to Production environments |
| `BC_DEPLOYMENT_JOURNAL_DIRECTORY` | No | `%LOCALAPPDATA%/BcDeployment/journal` | Recovery journal location |

Separate multiple roots with the platform path separator. On Windows, use the semicolon
separator. Elsewhere, use the colon separator.

`BC_ALLOWED_APP_ROOTS` and `BC_ALLOWED_SYMBOL_ROOTS` are local filesystem safety
boundaries. They control which files the server may read and write. They do not select
or authorize Business Central environments.

## Production deployments

Production is off by default and requires two independent opt-ins:

1. **The operator** starts the server with `BC_ALLOW_PRODUCTION_DEPLOYMENT=true`.
2. **The caller** passes `confirmProduction: true` on that specific `bc_deploy_pte` call.

If either is missing, the request is rejected during target validation. This happens
before the package is opened and before anything is uploaded. Read-only tools
(`bc_list_environments`, `bc_get_app_details`, `bc_download_symbol`) always work against
Production and are unaffected by this setting.

Leave the variable unset on developer workstations. Enable it only where releasing to
Production is genuinely the intent.

## Tool reference

### `bc_list_environments`

Lists the tenant's Business Central environments with name, type, country, status, and
Business Central version. Pass an optional exact `environmentName` and/or `countryCode`
such as `NL` to filter, or omit both to return everything. This tool is read-only. It
includes Production environments and omits the Admin Center friendly name.

### `bc_get_app_details`

Takes one `appId` and one required `environmentName`, queries only that environment, and
returns its matching installation: current app version and state, the last update
operation ID and result, and environment details such as type, status, country, and
Business Central version. If the app is not installed there, `found` is `false`.

```json
{
  "appId": "bfdd2605-acbd-40b9-9b74-8990250e4bd3",
  "environmentName": "SANDBOX-DE-DEV"
}
```

### `bc_get_operation_details`

Gets one app operation from the Admin Center API. The operation route requires the
environment name and app ID as well as the operation ID. The result includes `type`,
`status`, `sourceVersion`, `targetVersion`, `createdOn`, `startedOn`, `completedOn`,
`errorMessage`, and `scheduleKind`.

Use this read-only tool as a fallback when a deployment call was canceled, timed out, or
its result was lost. For a completed `bc_deploy_pte` call, use the operation already
included with each app result instead of issuing another request.

```json
{
  "environmentName": "PROD-DE",
  "appId": "bfdd2605-acbd-40b9-9b74-8990250e4bd3",
  "operationId": "2f410173-dbc4-4b6e-b859-f3cf280e7f86"
}
```

### `bc_download_symbol`

Downloads installed app symbol packages by app ID from an explicitly named Sandbox or
Production environment. To avoid endpoint rate limits, no more than 4 downloads run at
a time. The configurable limit ranges from 1 to 4 and defaults to 4. Each package is
streamed with a 150 MB limit. The server validates each package as NAVX and compares it
with its requested app ID. It then moves the package atomically into the symbol cache.

`symbolDirectory` is required and must be inside `BC_ALLOWED_SYMBOL_ROOTS`. An existing
identical package returns `alreadyPresent`. A different package with the same publisher,
name, and version requires `force: true`.

```json
{
  "appIds": [
    "bfdd2605-acbd-40b9-9b74-8990250e4bd3",
    "9ba7aa00-cafd-40ca-b05c-0a185e7b8a38"
  ],
  "environmentName": "PROD-DE",
  "symbolDirectory": "C:/src/MyExtension/.alpackages",
  "maxParallelDownloads": 4
}
```

The combined result preserves the requested app order and reports `succeeded`, `partial`,
or `failed`. Each app includes its status and either the downloaded package identity,
final path, byte size, SHA-256 hash, and manifest dependencies, or an error message.
Duplicate and empty app IDs are rejected before any request is sent.

### `bc_deploy_pte`

Checks live Admin Center environment metadata before opening the package. Unknown
environments are rejected, and Production targets are subject to the two gates described
above.

```json
{
  "packagePath": "artifacts/MyExtension.zip",
  "environmentNames": ["Sandbox East", "Sandbox West"],
  "deploymentSchedule": "Immediate",
  "syncMode": "Add",
  "languageId": "en-US",
  "acceptIsvEula": true,
  "installOrUpdateNeededDependencies": true,
  "confirmProduction": false,
  "operationTimeoutSeconds": 1800,
  "pollIntervalSeconds": 10
}
```

Multi-app bundles support every deployment schedule. `Immediate` waits for each app to
succeed before uploading its dependent app. Deferred schedules submit apps in dependency
order and return each accepted operation as `scheduled`. Admin Center controls when those
operations execute. Admin Center permits `NextMinorUpdate` and `NextMajorUpdate` only for
updates to installed PTEs.

`acceptIsvEula: true` accepts the app provider's EULA and the Microsoft terms documented
for the Admin Center App Management API. `syncMode: "ForceSync"` can cause data loss and
should only be chosen deliberately.

Each uploaded app result includes its complete final Admin Center `operation`, including
the exact `createdOn`, `startedOn`, and `completedOn` timestamps when supplied by the API.

## Long-running calls

`operationTimeoutSeconds` controls how long the server polls each immediate Business
Central operation. It does not control the MCP client's request lifetime.

VS Code does not guarantee that one synchronous tool call stays active for ten minutes.
Progress notifications show activity but do not extend a client timeout. If the client
cancels, polling stops, but any upload already accepted by Business Central continues
there. The recovery journal exists for exactly this case.

## Recovery journal

Each deployment returns a `deploymentId`. Its journal is written to
`<deploymentId>.jsonl` in `BC_DEPLOYMENT_JOURNAL_DIRECTORY`.

Entries are appended immediately after Admin Center accepts each upload, before status
polling begins. If the MCP call is cancelled or lost, use the recorded `environmentName`,
`appId`, and `operationId` with `bc_get_operation_details` to recover the operation.

Journal entries do not contain access tokens, package paths, request headers, or package
contents.

## Security

This server holds credentials that can modify Business Central tenants. Logging goes to
stderr only, so MCP messages on stdout stay valid, and exceptions are sanitized before
they reach the client.

To report a vulnerability, see
[SECURITY.md](https://github.com/mundanebcdev/BusinessCentralDeploymentTool/blob/main/SECURITY.md).
Do not open a public issue.

## Build from source

```powershell
git clone https://github.com/mundanebcdev/BusinessCentralDeploymentTool.git
cd BusinessCentralDeploymentTool
dotnet build BusinessCentralDeploymentTool.slnx
dotnet test BusinessCentralDeploymentTool.slnx
```

The repository's own `.vscode/mcp.json` runs the server from source with `dotnet run`,
which is the fastest loop when changing tool behavior.

## Hosting

This is a local stdio server. Running it as a shared Azure service would require an
authenticated Streamable HTTP transport and durable shared journal storage, which are
intentionally out of scope here.

## Contributing

Issues and pull requests are welcome. See
[CONTRIBUTING.md](https://github.com/mundanebcdev/BusinessCentralDeploymentTool/blob/main/CONTRIBUTING.md)
for the workflow and the invariants that changes must preserve.

## License

[MIT](https://github.com/mundanebcdev/BusinessCentralDeploymentTool/blob/main/LICENSE)
© Maksym Kravchenko.

This project is not affiliated with or endorsed by Microsoft.
