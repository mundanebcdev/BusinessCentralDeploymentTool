## Summary

<!-- What does this change and why. Link the issue it closes. -->

Closes #

## Type of change

- [ ] Bug fix
- [ ] New feature
- [ ] Breaking change
- [ ] Documentation
- [ ] Build, CI, or dependencies

## Checklist

- [ ] `dotnet build BusinessCentralDeploymentTool.slnx` succeeds with no new warnings
- [ ] `dotnet test BusinessCentralDeploymentTool.slnx` passes
- [ ] Tests cover the changed behavior
- [ ] `CHANGELOG.md` updated under `## [Unreleased]`
- [ ] README tool reference and `.mcp/server.json` updated if inputs changed

## Safety review

Confirm the change preserves these properties, or explain why it does not need to:

- [ ] Production deployment still requires both `BC_ALLOW_PRODUCTION_DEPLOYMENT` and `confirmProduction`
- [ ] Environment metadata is still validated before any package is opened or uploaded
- [ ] Filesystem access stays inside `BC_ALLOWED_APP_ROOTS` and `BC_ALLOWED_SYMBOL_ROOTS`
- [ ] No secrets, tokens, or package paths are written to the journal or logs
- [ ] Accepted operations are still journaled before polling begins

## Verification

<!-- How you tested this. If you ran against a real sandbox, say so, and redact identifiers. -->
