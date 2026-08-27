# UPilot Repository Instructions

## Release Workflow

- Treat commands such as `发布版本 v0.3.22，并检查打包结果和版本号是否正确`, `发布 v0.3.22`, `触发发布版本 0.3.22`, and semantically equivalent requests as explicit authorization to run the GitHub Actions workflow named `发布版本` from `.github/workflows/prepare-release.yml`.
- Use the requested semantic version after normalizing it to `vMAJOR.MINOR.PATCH`. If the user requests the next version without specifying a number, inspect existing tags and use the next patch version unless the user states otherwise.
- Trigger the workflow from `main` with `gh workflow run prepare-release.yml --ref main -f version=<version>`. Do not manually create or push the release tag, and do not manually maintain release versions in `package.json` or `upilotserver~/pyproject.toml`; the workflow owns those operations.
- Before triggering a release, verify that the intended release changes are committed and pushed to `main`. Do not include unrelated local changes without explicit authorization.
- After triggering, monitor both the `发布版本` run and its downstream `Build UPilot MCP Server EXE` run until completion. A successful dispatch alone is not a successful release.
- Verify that the release tag exists, the GitHub release and expected assets were published, and the version in the tag's `package.json` and `upilotserver~/pyproject.toml` exactly matches the requested version. Report run URLs, commit/tag information, asset results, and any version mismatch or failed job clearly.
- If either workflow fails or any version is inconsistent, do not report release success. Preserve the failure logs and identify the failed step and corrective action.

## Testing And Validation

- Use `./Tests~/UPilotTest` as the default and canonical Unity project for UPilot package compile and EditMode acceptance.
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
