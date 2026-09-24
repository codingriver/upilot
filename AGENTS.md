# UPilot Repository Instructions

## Release Workflow

- Before handling this repository's release, release-status monitoring, or release-failure diagnosis, read the relevant sections of `Documentation~/Release.md`. The runbook supplies procedures, not permission to publish, retry, change files, or delete tags; a diagnosis-only request remains read-only.
- Treat commands such as `发布版本 v0.3.22，并检查打包结果和版本号是否正确`, `发布 v0.3.22`, `触发发布版本 0.3.22`, and semantically equivalent requests as explicit authorization to run the GitHub Actions workflow named `发布版本` from `.github/workflows/prepare-release.yml`.
- Use the requested semantic version after normalizing it to `vMAJOR.MINOR.PATCH`. If the user requests the next version without specifying a number, inspect existing tags and use the next patch version unless the user states otherwise.
- Trigger the workflow from `main` with `gh workflow run prepare-release.yml --ref main -f version=<version>`. Do not manually create or push the release tag, and do not manually maintain release versions in `package.json` or `upilotserver~/pyproject.toml`; the workflow owns those operations.
- Unity release evidence is optional. When `-f acceptanceRunId=<runId>` is supplied, verify its provenance, age and source identity; a missing evidence run must not block release dispatch. Do not substitute a local summary for explicitly supplied evidence. See `Documentation~/UP001-UP009-Delivery.md` for the optional evidence workflow and source C→R contract.
- Before triggering a release, verify that the intended release changes are committed and pushed to `main`. Do not include unrelated local changes without explicit authorization.
- Preserve the repository's established check policy: do not enable optional checks, disable required checks, or change strictness just to retry or diagnose a release without an explicit requirement. Never report a skipped, disabled, or unknown check as passed.
- After triggering, monitor both the `发布版本` run and its downstream `Build UPilot MCP Server EXE` run until completion. A successful dispatch alone is not a successful release.
- Before reporting release success, verify both workflow conclusions, the tag-to-build commit identity, the GitHub release and expected assets, and that the tag's `package.json`, `upilotserver~/pyproject.toml`, and release `manifest.json` versions match the requested version. Confirm the build's local EXE/manifest/checksum and `--version` verification passed before publication; observe the remote asset list, but do not claim to have verified downloaded EXE bytes unless that was separately done. Report run URLs, commits/tag, asset and local hash results, and any unverified item clearly. Packaging/release success is not Unity compilation, EditMode, or business acceptance: report only coverage backed by actual evidence.
- If either workflow fails or any version is inconsistent, do not report release success. Preserve the failure logs and identify the failed step and corrective action.
- Do not automatically delete, move, or recreate a remote tag or overwrite a Release after failure. Recovery requires appropriate user authorization; when a new release is authorized, prefer a new patch version over reusing a failed tag. If dispatch or publication is uncertain, inspect the original run, tag, and Release before considering another request; do not automatically redispatch.

## Testing And Validation

- Before changing code or tests to address CI failures, use the failed step, its logs, and source identity to distinguish test fixture/environment assumptions, check-policy changes, generated-artifact drift, and product defects. Mark unsupported conclusions as unconfirmed; do not remove valid assertions, weaken exact project identity restrictions, or bypass required gates merely to make CI green.
- Use `./Tests~/UPilotTest` as the default and canonical Unity project for UPilot package compile and EditMode acceptance.
- `./Tests~/UPilotTest2022` is the explicitly authorized repository Unity 2022.3 acceptance project. Use it for the Unity 2022.3 targeted matrix, verifying the actual project path at its HTTP MCP endpoint; the original `UPilotTest` remains the default. Do not authorize other projects by basename or weaken the exact-path acceptance allowlist.
- After UPilot C# or assembly-related changes, validate against `./Tests~/UPilotTest` before claiming Unity compile/EditMode acceptance, unless the user explicitly says not to run tests.
- Do not attempt a full EditMode suite by default. Unless the user explicitly requests a full/complete EditMode regression, run only the narrowest targeted tests that cover the changed code and its direct regressions.
- A full EditMode run is authorized only when the user explicitly asks for full, complete, regression, acceptance, or equivalent whole-suite validation; otherwise report targeted results and any unrun coverage.
- If a full-suite run is not explicitly authorized, do not start it speculatively after targeted tests pass, even when preparing a final handoff.
- Do not use external client projects such as `D:\MA\xclient` or `F:\xclient2` as default UPilot validation projects.
- Use external client projects only when the user explicitly requests project-side/business smoke validation or investigation.

## UPilot Improvement Backlog

- During testing or development, if a repeated issue, missing capability, fragile workflow, weak diagnostic, or manual step could be simplified by improving UPilot features, MCP tools, agent rules, or project integration, record it in the repository-root `TODO_UPilot.mcd`.
- Keep each item actionable: include the observed problem, affected workflow/tool, proposed UPilot or integration improvement, reproduction or evidence when available, and current status.
- Do not bury UPilot improvement ideas only in external client project TODO files; the root `TODO_UPilot.mcd` is the source of truth for UPilot product/backlog follow-up.
