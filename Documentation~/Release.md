# UPilot release runbook

## 1. Scope and authority

The sole release entry point is the GitHub Actions workflow **发布版本**
(`.github/workflows/prepare-release.yml`). Its downstream workflow is **Build UPilot MCP Server EXE**
(`.github/workflows/build-server-exe.yml`). These workflows and their validation scripts define
actual behavior; this runbook explains how to operate and diagnose them. Repository release
authority and acceptance boundaries come from the root `AGENTS.md`, not from this document.
An explanation, status request, or failure diagnosis authorizes read-only investigation, not
dispatch, retries, source edits, tag changes, or Release changes.

Work from the repository root identified by Git (`git rev-parse --show-toplevel`), not a
machine-specific drive or checkout path. Unity Package Manager reads `package.json` from the
exact tag commit: updating it after creating the tag cannot correct an already tagged package.
The preparation workflow owns the version update, derived-file generation, release commit, and tag.

## 2. Before dispatch and release flow

1. Check `git status --short`, the current branch, `git fetch origin main --tags`, and the
   intended commits against `origin/main`. Confirm all intended changes are committed and
   pushed to `main`; leave unrelated working-tree changes out of the release. Do not edit
   `package.json` or `upilotserver~/pyproject.toml` manually to prepare a release.
2. Inspect remote tags (`git ls-remote --tags origin 'refs/tags/v*'`). Normalize a requested
   version to `vMAJOR.MINOR.PATCH`; if no number was requested, use the next patch after the
   highest existing version tag unless directed otherwise. A failed tag remains occupied even
   if no EXE or Release asset was published.
3. Inspect or run the existing non-publishing source quality check against the selected
   source commit, without inventing a mandatory full Unity suite. Record its run/source identity
   and which checks were disabled, unknown, or actually passed. The workflow repeats its own
   source quality gate before updating the version.
4. If an `acceptanceRunId` was explicitly supplied, retain it and verify its source, age,
   workflow, and provenance. Missing evidence does not block release; supplied invalid evidence
   must not be silently omitted or replaced with a local summary. See
   `Documentation~/UP001-UP009-Delivery.md` for the optional Unity evidence contract and the
   remaining end-to-end evidence validation limit in section 5 below.
5. Only when publication is authorized, dispatch from `main`:

   ```text
   gh workflow run prepare-release.yml --ref main -f version=<vMAJOR.MINOR.PATCH>
   ```

   If explicitly supplied, add `-f acceptanceRunId=<runId>` to that command. Preserve the
   resulting prepare run ID, its input version, and its `headSha`; do not infer its identity
   solely from the newest run in a list. A dispatch with an uncertain result must be observed
   before considering another dispatch.

The preparation sequence is: **source/optional-evidence gate → synchronize UPM and Python
versions → render version-bound Skill artifacts → validate versions and generated output →
commit the version and generated artifacts → push the tag → explicitly dispatch the downstream
build**. The release commit may include `skills/upilot-unity-mcp/SKILL.md`,
`skills/upilot-unity-mcp/agents/openai.yaml`, and
`Documentation~/AgentRules/AGENTS.upilot.md` alongside the two version files. The build
workflow also listens for a tag push; observe both relevant runs if the two triggers produce
separate builds. A successful preparation run or existing tag is not a finished release.
Version-bound generation in this workflow does not require connecting to Unity or synchronizing
external projects. When separately modifying UPilot template sources or installed integrations,
follow the existing `skills/upilot-unity-mcp/references/installation.md` maintenance procedure.

## 3. Monitor and verify the release

For each known run ID, use `gh run view <runId> --json status,conclusion,headSha,url`,
`gh run watch <runId> --exit-status`, and, for failures, `gh run view <runId> --log-failed`.
If an ID is not yet known, `gh run list --workflow prepare-release.yml --event workflow_dispatch`
can help locate candidates, but select by version, time, ref, and commit instead of assuming
the newest run is yours. Resolve the downstream run by tag/ref and verify its `headSha` equals
the commit reached by `git rev-parse 'vMAJOR.MINOR.PATCH^{commit}'`. Retain both run URLs.
Do not replay dispatch to obtain a result from an existing run.

Before reporting success, verify all of the following:

- Both preparation and corresponding downstream build have terminal success; the downstream
  tag, build source commit, and requested version are the same release. Other tag-triggered
  builds should be reported separately if present.
- The tag's `package.json` version and `upilotserver~/pyproject.toml` project version exactly
  match the requested version. The published `manifest.json` has matching `upmVersion` and
  `serverVersion`.
- The build log's **Verify local release assets and EXE identity** step passed, and its
  `local-release-verification.json` and `exe-version.log` record the expected EXE version,
  commit, channel, protocol, size and matching local EXE/checksum/manifest hashes.
- The GitHub Release for that tag includes `manifest.json`,
  `upilot-mcp-server-<version>-win-x64.exe`, and its `.exe.sha256` file. The build verifies
  the local assets before upload; observing the Release asset list does not independently
  verify the bytes of remotely downloaded assets.
- Report Unity compile/EditMode/business acceptance separately, including whether it ran,
  its source identity and evidence coverage. Release packaging does not establish it.

If `gh release view <tag>` temporarily shows no assets, inspect that same Release by ID with
`gh api repos/<owner>/<repo>/releases/<release-id>/assets` and try a bounded recheck
for that same ID/tag. If assets still cannot be confirmed, say release verification
is incomplete. Do not publish again merely because one interface returned an empty list.

## 4. Failure diagnosis

Locate the failed step and its source commit first. Obtain that run's source-bound quality
artifact (for example, `upilot-pre-release-quality-<sha>` or `upilot-build-quality-<sha>`)
and read `artifacts/reliability-quality/quality.json` before the relevant `contracts.log`,
`skill.log`, or documentation details. Preserve the run ID, source identity, failed step,
original error, and needed corrective action; redact tokens and other secrets from reports.
If no artifact exists, use the failed run's step logs and mark unavailable evidence clearly.
Download an identified artifact with `gh run download <runId> --name <artifact-name> --dir <output-dir>`;
use a dedicated output directory and never confuse files from separate runs.

| Symptom | Check first | Interpretation and boundary |
| --- | --- | --- |
| `UPILOT_ACCEPTANCE_PROJECT_MISMATCH` | Test fixture path, checkout root, actual project identity | Fix incorrect fixture inputs; retain the exact project allowlist. |
| `generated template artifact is stale` | Source template, rendering context and version, generated bytes and line endings | Distinguish stale content from LF/CRLF checkout changes; do not hand-edit generated output. |
| Linux succeeds, Windows fails | Matching source commits, Git attributes, checked-out file bytes | Linux success does not prove Windows checkout or build success. |
| `docs.registry` fails | Invalid or duplicate names, proxy handler gaps, documented Registry version | Diagnose the specific mismatch; do not change ToolStatus solely because of a generic hint. |
| `docs.todo-id` fails | Whether explicitly enabled, expected plan file, current check requirement | Do not fabricate a plan file or alter the check policy just to pass the gate. |
| Preparation passes, no Release | Exact downstream run(s), tag/ref and commit, failed step | A created tag is not a successful EXE release. |
| Assets seem missing | Same Release ID asset API and expected asset names | Separate temporary observation differences from an actual missing upload; remote EXE download is not part of this release gate. |
| Dispatch times out or its result is uncertain | Existing run IDs, tag and Release state | Observe existing state before any new authorized action; do not redispatch automatically. |

Classify failures as fixture/environment assumptions, check policy, generated-artifact drift,
or product defects based on the logs. Mark unsupported causes as unconfirmed. Do not remove
valid tests, weaken project identity, or disable required gates to make a run green.
Do not automatically delete, move, or recreate a failed remote tag, or overwrite a Release.
Any destructive recovery needs separate authorization; for an authorized new release, prefer
the next patch version. No diagnosis-only request authorizes repair or another release.

## 5. Optional checks and known evidence limitation

`docs.registry` and `docs.todo-id` are **off by default** in the release-quality CLI.
Enable them only when required with `--enable-docs-registry` or `--enable-docs-todo-id`.
Do not substitute `--skip-docs` for those defaults: it skips other documentation checks too.
Do not label disabled, skipped, or `unknown` checks as passed or change `--docs-strict`
merely to obtain a green result. The CLI and workflow remain authoritative for the current
executed check set; these instructions do not change the gate implementation.

Optional Unity evidence remains optional when no run ID or tag marker is supplied. An
explicit run ID or tag marker triggers the trusted workflow evidence check even without
`--require-unity-summary`; malformed, conflicting or invalid claims fail the build. The
C→R verifier accepts a direct single-parent version commit only if both version files
retain their other bytes and all version-bound generated files match deterministic rendering
from the checked source. These behaviors have local regression coverage; a real controlled
Unity evidence run covering C→R has **not yet been validated end to end**. Do not claim
that it has, or substitute a local summary for controlled evidence.

## 6. Result report template

Report these fields, using **not checked**, **unknown**, or **failed** rather than guessing:

- Requested version and overall result: build succeeded, assets published, release fully
  verified, or incomplete/failed (state which).
- Preparation run ID/URL/conclusion and downstream run ID(s)/URL(s)/conclusions.
- Main source commit, tag commit, and matching downstream build commit.
- Tag UPM/Python versions, manifest UPM/Server versions, and observed EXE version.
- Three expected published assets, local EXE SHA256, checksum-file value and manifest digest;
  state explicitly if no remote asset download/hash verification was performed.
- Documentation checks and Unity evidence actually executed, their identities and coverage.
- Failed step and confirmed cause, remaining unknowns, and any action requiring new authority.

Leave detailed fault history in the run artifacts and existing product backlog; do not
duplicate it as another release-status list in this runbook.
