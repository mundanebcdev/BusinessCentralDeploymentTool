# Agent instructions

Copy this into your own repository's `.github/copilot-instructions.md`, `AGENTS.md`,
or `CLAUDE.md` so the agent uses this MCP server correctly.

```markdown
# Business Central deployment

- Use `bc_deploy_pte` for PTE deployments.
- Use `bc_get_app_details` with an app ID and one explicit environment name. Never use it to query every environment.
- Use `bc_get_operation_details` with the environment name, app ID, and operation ID only to recover a deployment whose original call was canceled, timed out, or lost.
- Make one tool call containing the package path and every target environment. Do not make separate calls per environment.
- Pass ZIP bundles directly. The server validates and orders bundled apps by dependency.
- Use any supported deployment schedule for multi-app bundles. The server submits apps in dependency order. With `Immediate`, it also waits for each dependency to succeed before submitting its dependent app.
- Use `NextMinorUpdate` and `NextMajorUpdate` only for updates to installed PTEs. Admin Center rejects these schedules for new PTE installations.
- Set `acceptIsvEula` to `true` only when the user's deployment request authorizes acceptance of the documented app-provider and Microsoft Marketplace terms.
- Never set `confirmProduction` to `true` on your own initiative. Set it only when the user names a Production environment and explicitly authorizes the release in that turn. If the server rejects the call because `BC_ALLOW_PRODUCTION_DEPLOYMENT` is not enabled, report that and stop. Do not suggest workarounds.
- Treat `ForceSync` as potentially destructive and use it only when the user explicitly requests it.
- Report the combined result, including failed environments, operation IDs, and exact completion timestamps from each returned operation. Do not poll through additional MCP calls after a complete result.
- Do not promise that the editor will keep a long-running tool call alive. Accepted Business Central operations can continue after client cancellation. Use the recovery journal when needed.
```

## Why these rules exist

**One call, many environments.** The server deploys environments in parallel and
returns a combined result with per-environment status. Splitting the work into one
call per environment is slower and produces a fragmented report.

**Dependency ordering is automatic.** A ZIP bundle is read, validated, and topologically
sorted from the NAVX manifests. Uploading apps individually in a guessed order risks a
dependency failure. Deferred schedules preserve submission order, but Admin Center controls
when the accepted operations execute.

**Production is deliberately hard to reach.** It requires the operator to have started
the server with `BC_ALLOW_PRODUCTION_DEPLOYMENT=true` *and* the caller to pass
`confirmProduction: true`. The second gate exists specifically so an agent cannot
release to Production without the user asking for it in that turn.

**Polling is already handled.** For `Immediate` deployments the server waits for each
Admin Center operation to reach a terminal state before returning. Additional
calls to monitor progress waste turns. Use `bc_get_operation_details` only as the recovery
fallback described above.

**Cancellation is not rollback.** If the MCP client times out, uploads already accepted
by Business Central continue there. The returned `deploymentId` maps to a JSON Lines
journal holding the accepted operation IDs, which is how you recover the outcome.
Pass the journal's environment name, app ID, and operation ID to
`bc_get_operation_details` to get the complete operation.
