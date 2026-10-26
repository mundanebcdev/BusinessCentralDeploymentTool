# Security Policy

## Supported versions

Security fixes are applied to the latest released minor version only.

| Version | Supported |
| --- | --- |
| 1.0.x | Yes |

## Reporting a vulnerability

**Do not open a public issue for a security problem.**

Report it privately through GitHub:

1. Go to the [Security Advisories page](https://github.com/mundanebcdev/BusinessCentralDeploymentTool/security/advisories/new).
2. Describe the issue, the affected version, and how to reproduce it.
3. Include the impact you believe it has.

You will get an acknowledgement of the report. Once a fix is available, a patched
release is published and the advisory is disclosed with credit unless you ask to
remain anonymous.

When reporting, please **redact tenant IDs, client IDs, secrets, certificates,
access tokens, environment names, and customer data** from logs and screenshots.

## Scope

This project holds credentials that can modify Business Central tenants. Findings
in the following areas are of particular interest:

- Credential handling in [Authentication/CredentialFactory.cs](Authentication/CredentialFactory.cs),
  including any path that could write a secret to a log or to the journal.
- Escapes from the `BC_ALLOWED_APP_ROOTS` or `BC_ALLOWED_SYMBOL_ROOTS` allowlists,
  including symlink, junction, and path traversal cases.
- Bypasses of the Production deployment gate.
- Archive handling in [Packages/PackageBundleReader.cs](Packages/PackageBundleReader.cs),
  such as zip slip or decompression bombs.
- Downloaded symbol package handling in [Symbols/SymbolDownloadService.cs](Symbols/SymbolDownloadService.cs).
- Leakage of tokens or tenant data into MCP responses, stderr logs, or the journal.

Out of scope:

- Vulnerabilities in the Business Central Admin Center API itself. Report those to
  [Microsoft MSRC](https://msrc.microsoft.com/report).
- Misconfiguration by an operator, such as pointing `BC_ALLOWED_APP_ROOTS` at a
  directory that untrusted users can write to.
- The fact that an operator who enables `BC_ALLOW_PRODUCTION_DEPLOYMENT` can deploy
  to Production. That is the documented purpose of the setting.

## Operating this server safely

- Prefer certificate authentication over a client secret.
- Grant the Entra application only `AdminCenter.ReadWrite.All`.
- Keep `BC_ALLOW_PRODUCTION_DEPLOYMENT` unset unless the workstation is genuinely
  meant to release to Production.
- Scope `BC_ALLOWED_APP_ROOTS` to the narrowest build output directory that works.
- Treat the deployment journal as an operational record; it holds environment
  names, app IDs, and operation IDs, but no credentials.
