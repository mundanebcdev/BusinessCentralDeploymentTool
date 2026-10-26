# Contributing

Thanks for taking the time to contribute. This project is a local MCP server that
performs real deployments against Microsoft Dynamics 365 Business Central tenants,
so changes are reviewed with an emphasis on safety and predictability.

## Getting started

```powershell
git clone https://github.com/mundanebcdev/BusinessCentralDeploymentTool.git
cd BusinessCentralDeploymentTool
dotnet restore
dotnet build BusinessCentralDeploymentTool.slnx
dotnet test BusinessCentralDeploymentTool.slnx
```

The repository pins the .NET SDK in [global.json](global.json). Install a matching
SDK from <https://dotnet.microsoft.com/download>.

Tests use MSTest on Microsoft.Testing.Platform. They are fully offline: the Admin
Center API is exercised through a stubbed `HttpMessageHandler`, and packages are
built in a temporary directory. **Never add a test that calls a live tenant.**

## Making a change

1. Open an issue first for anything larger than a bug fix, so the design can be
   agreed before you spend time on it.
2. Create a branch from `main`.
3. Keep the change focused. Unrelated refactors make review harder.
4. Add or update tests for behavior you change.
5. Update [CHANGELOG.md](CHANGELOG.md) under `## [Unreleased]`.
6. Run `dotnet build` and `dotnet test` before opening the pull request.

## Coding conventions

- Warnings are errors. The build will fail on a new warning.
- Nullable reference types are enabled; do not silence them with `!` unless the
  invariant is genuinely guaranteed and obvious at the call site.
- Follow the existing style: file-scoped namespaces, primary constructors for
  services, `sealed` types by default, and `record` types for API models.
- Comments explain *why*, not *what*. Most code here does not need any.
- Formatting is enforced by [.editorconfig](.editorconfig). Run `dotnet format`
  if your editor does not apply it automatically.

## Rules that changes must not break

These are the safety properties the project exists to guarantee. A pull request
that weakens one of them will not be merged without a strong justification.

- **Production stays opt-in twice.** A Production environment requires both
  `BC_ALLOW_PRODUCTION_DEPLOYMENT=true` on the server and `confirmProduction: true`
  on the call. Neither may become the default.
- **Environment metadata is validated before the package is opened.** Target
  validation must stay ahead of any file read or upload, so a rejected request
  never touches disk or the tenant.
- **Filesystem access stays inside the allowlist.** Packages are only read from
  `BC_ALLOWED_APP_ROOTS` and symbols are only written to `BC_ALLOWED_SYMBOL_ROOTS`.
  Path handling must resolve and re-check, not merely string-prefix compare.
- **The journal never records secrets.** No tokens, headers, package paths, or
  package contents.
- **Accepted operations are journaled before polling starts**, so a cancelled MCP
  call is still recoverable.
- **Errors are sanitized** before they reach the MCP client.

## Adding a tool

New MCP tools go in [Tools/BcDeploymentTools.cs](Tools/BcDeploymentTools.cs) with
the real work in a service class. Every parameter needs a `[Description]` that is
written for a language model: state the units, the accepted range, and the
consequence of the value. Destructive options must default to the safe value and
require an explicit opt-in.

If a tool changes its inputs, update the README tool reference and
[.mcp/server.json](.mcp/server.json) in the same pull request.

## Reporting security issues

Do not open a public issue. See [SECURITY.md](SECURITY.md).

## License

By contributing, you agree that your contributions are licensed under the
[MIT License](LICENSE).
