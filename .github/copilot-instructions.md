# Copilot instructions for this repository

These notes are for agents working **on** this codebase. For guidance on driving the
deployed MCP server, see [docs/agent-instructions.md](../docs/agent-instructions.md).

## What this is

A .NET 10 local MCP server (stdio) that wraps the Business Central Admin Center API
v2.29. It exposes five tools from [Tools/BcDeploymentTools.cs](../Tools/BcDeploymentTools.cs):
`bc_list_environments`, `bc_get_app_details`, `bc_get_operation_details`,
`bc_download_symbol`, and `bc_deploy_pte`.

## Layout

| Path | Contents |
| --- | --- |
| `Tools/` | MCP tool surface. Thin; delegates to services. |
| `AdminCenter/` | Admin Center HTTP client and API models |
| `Deployment/` | Deployment orchestration, result shapes, recovery journal |
| `Packages/` | NAVX `.app` parsing, ZIP bundle reading, dependency ordering |
| `Symbols/` | Dev-endpoint client and symbol download service |
| `AppDetails/` | Installed-app lookup |
| `Authentication/` | Credential construction from configuration |
| `Configuration/` | `AdminCenterOptions`, built from environment variables |
| `.mcp/server.json` | MCP registry manifest; declares every environment variable |

## Build and test

```powershell
dotnet build BusinessCentralDeploymentTool.slnx
dotnet test BusinessCentralDeploymentTool.slnx
```

Tests are MSTest on Microsoft.Testing.Platform. The SDK is pinned in `global.json`.

Warnings are errors. Style is enforced by `.editorconfig`; run `dotnet format` before
committing.

## Conventions

- File-scoped namespaces, `sealed` types, primary constructors for services,
  `record` types for API models.
- Services are registered as singletons in [Program.cs](../Program.cs) and resolved
  by type, so constructor parameter order is free.
- Logging goes to **stderr only**. Stdout carries the MCP protocol; writing to it
  corrupts the session.
- Tool parameter `[Description]` text is read by a language model. State units,
  ranges, and consequences.

## Testing rules

- Tests must stay offline. Use the `StubHttpMessageHandler` pattern in
  [tests/BcDeployment.Tests](../tests/BcDeployment.Tests) and build packages with
  `TestPackageFactory` into a temp directory.
- Never add a test that reaches a live Business Central tenant.
- Assert on behavior, not on wording of documentation or file placement.

## Invariants that must not regress

- Production requires both `BC_ALLOW_PRODUCTION_DEPLOYMENT=true` and a per-call
  `confirmProduction: true`. Both default to off.
- `ValidateDeploymentTargetsAsync` runs before the package is opened, so a rejected
  request never reads a file or contacts the tenant with a payload.
- Package reads are confined to `BC_ALLOWED_APP_ROOTS`; symbol writes to
  `BC_ALLOWED_SYMBOL_ROOTS`.
- The journal records accepted operation IDs **before** polling starts, and never
  contains tokens, headers, package paths, or package contents.
- Exceptions are sanitized before being returned to the MCP client.

## When changing tool inputs

Adding a tool, or adding a parameter to an existing tool, is **strictly prohibited
without explicit user approval**. Ask first. If you do add one, say so explicitly in
your response — no silent additions.

Once approved, update all three together: the `[Description]` attributes, the README tool
reference, and `.mcp/server.json`. Add a `CHANGELOG.md` entry under `## [Unreleased]`.

