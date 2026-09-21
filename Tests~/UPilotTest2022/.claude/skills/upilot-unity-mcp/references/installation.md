# Installation

1. Verify the Unity project root contains `Packages/manifest.json`.
2. Add `io.github.codingriver.upilot` using an explicit stable release tag. For automated installation, pass the intended tag with `--upm-ref`, or use `--use-local-upm` when validating a local checkout.
3. If running the MCP Server from Python instead of the packaged executable, install the Python package from `upilotserver~` with `--setup-python`.
4. Install the shared repository Skill for the intended clients. Codex, Cursor, and OpenCode use `.agents/skills/upilot-unity-mcp`; Claude Code uses `.claude/skills/upilot-unity-mcp`.
5. Configure one MCP service named `upilot` at `http://127.0.0.1:8011/mcp`.
6. Open Unity and verify project identity with `unity_mcp_status`.

Automated install:

```bash
python skills/upilot-unity-mcp/scripts/install_upilot.py \
  --unity-project <UNITY_PROJECT_ROOT> \
  --upm-ref <STABLE_RELEASE_TAG>
```

The installer always synchronizes all four supported Agents. Codex, Cursor, and OpenCode deliberately share the `.agents/skills` copy instead of creating separate managed copies under `.cursor/skills` or `.opencode/skills`; Claude uses the independent `.claude/skills` copy. `--skill-client` remains accepted for command-line compatibility but no longer limits these authoritative targets.

For a local repository checkout, replace `--upm-ref` with `--use-local-upm`. The installer deliberately has no default UPM version and does not infer one from `package.json`: a remote install must name its tag, branch, or commit explicitly, while the MCP Server may be distributed and versioned independently as an executable.

To write a Codex project registration, add `--write-codex-mcp project`. It writes only an HTTP URL. Use `--http-port` and `--mcp-name` when allocating a distinct endpoint for another Unity project. Never pass the Unity Bridge WebSocket port to a third-party AI client.

The core install keeps optional features disabled. When the user explicitly requests UPilot Flow, read `flow.md` before changing packages or scripting defines.

Do not overwrite a non-UPilot MCP registration. UPilot only changes its own named registration and managed integration targets.

Unity Editor Agent Setup writes `.upilot-install.json` into every Skill directory that it manages. First install, repeated install, package upgrade, and automatic refresh authoritatively synchronize the `upilot-unity-mcp` Skill directories from the current templates, even when a legacy, unmanaged, or locally customized copy is present. Before replacing an unverifiable or locally changed target, UPilot copies it to `<ProjectRoot>/.upilot/backups/agent-integrations/<timestamp>-<id>/` and verifies the backup hash. Clean older managed copies are replaced without a redundant backup. A backup failure blocks that target and leaves it unchanged while other independent targets continue.

Agent rules use the same policy only inside `<!-- upilot:start -->` and `<!-- upilot:end -->`. Project business rules outside that block are preserved. Locally changed or unverifiable managed blocks are backed up before repair. Automatic and ordinary manual synchronization do not prompt for a second customization decision.

Skill validation is read-only. Run `scripts/check_skill_pack.py` without arguments
to detect source versus a managed installation, or pass `--mode source|installed`
and `--root <SKILL_DIRECTORY>` explicitly. Source mode checks Unity `.meta` files
and repository contracts. Installed mode checks required files, references and
the recorded content hash without requiring Unity metadata or repository access.
Unmanaged or changed installations are reported as unverified/failed; the checker
never repairs files, changes endpoints or rewrites the installation marker.

## Unified Template Maintenance

Use the confirmed package-source Skill directory and its `template-manifest.json`.
The Agent template owns standing maintenance constraints; this reference owns the
procedure. Keep the existing UPilot Skill instead of adding a test-project-specific
parallel Skill.

| Change | Authoritative source |
|---|---|
| Package-wide UPilot Agent rules | `AGENTS.md.template` |
| UPilot Skill instructions | `SKILL.md.template` |
| Skill metadata | `agents/openai.yaml.template` |
| Distributed references, scripts, and other non-generated resources | Their package-source files, without additional templates |

Generated repository files, project managed blocks, installed Skill files, and installed
templates are outputs, not editable sources. Do not append package-wide UPilot instructions
to unmanaged project/parent rules or create installed Skill files by hand. Preserve genuine
business rules/Skills, repository release rules, parent inheritance, and `AGENT_Distill.md`.
If the authoritative source is unavailable, report the missing source and leave outputs
untouched rather than treating an installed copy as the source.

### Required completion of each editing batch

Perform this sequence within every authorized template/resource maintenance task, without
waiting for another request to update the project. Commands below run from the source
Skill directory, not an installed copy.

1. Edit the appropriate source and increment manifest versions: Agent behavior changes
   increment `agentRulesVersion`; every distributed file/template change increments
   `skillPackVersion`, including changes to `AGENTS.md.template`. Do not change the UPM
   package version as part of instruction maintenance.
2. Run `python scripts/render_skill_pack.py --write`, then
   `python scripts/render_skill_pack.py --check` and
   `python scripts/check_skill_pack.py --mode source`. Stop project synchronization if
   source generation or checks fail.
3. Confirm the authorized target project's absolute path. For UPilot repository
   maintenance, use `<UPILOT_SOURCE>/Tests~/UPilotTest`; do not automatically synchronize
   other test or external client projects. Call `unity_mcp_status`, require
   `connected=true/serverReady=true`, and match `paths.unityProjectAbsolute` to that path.
4. Call `unity_agent_integrations_check()` and inspect the template source, versions,
   context, and all five targets. The source must match the package source just edited;
   do not apply an unrelated installed package's templates. Preview is also available
   through `unity_agent_integrations_sync(apply=false)`.
   Before the first apply, collect the rule-preservation baseline described below.
5. With existing project write authorization, call `unity_agent_integrations_sync(apply=true)`.
   Keep automatic backups; inspect every target's status, hashes, backup path, and error,
   not only envelope `ok`. Synchronization must cover project `AGENTS.md`, the Cursor rule,
   `CLAUDE.md`, shared `.agents/skills/upilot-unity-mcp`, and independent
   `.claude/skills/upilot-unity-mcp`.
6. Run `python scripts/check_skill_pack.py --mode installed --root <SKILL_DIRECTORY>` for
   each of the two installed Skill directories. Confirm expected versions, project/port/
   package context, final hashes, and unchanged bytes outside rule managed blocks.
7. After the first successful synchronization and both installed checks, collect the
   repeat-sync baseline below. Perform one actual complete synchronization again and
   compare that baseline, then repeat the read-only complete check. Require all five
   targets to be `current`, with unchanged file content/timestamps and no new backups.
   Report completion only after these checks succeed; a read-only `current` result
   alone does not prove that an actual repeated synchronization performed no writes.

### Evidence baselines

Use read-only task-local snapshots; do not add a background watcher or permanent baseline
system. Keep two distinct comparison points:

- **Before the first apply:** record whether each of `AGENTS.md`, the Cursor rule and
  `CLAUDE.md` exists. For each existing, valid managed block, retain the raw prefix and
  suffix bytes or their separate SHA256 digests, including BOM and original newlines.
  Preserve parent-rule inheritance. If an existing file has no block, retain its original
  bytes and verify they remain intact when the installer appends the block. Record a
  missing file as missing; there are no previous outside bytes to compare. Malformed
  markers must fail through the existing synchronizer, not be repaired by hand.
- **After the first successful sync and installed validation, before the repeat apply:**
  record the relative file set, SHA256 and full-precision modification times for the three
  rule files, both Skills' managed files including each `.upilot-install.json`, and
  `.upilot/agent-integrations.json`. Compare exactly the same set after the repeated apply,
  detecting additions/removals as well as content or timestamp changes. Separately record
  and compare the directory list under `.upilot/backups/agent-integrations/`; an absent
  backup root is an empty list, not a reason to create it.

For Skill files, use the installer's managed-content filter, but explicitly include the
installation metadata in this evidence snapshot. Exclude Unity `.meta` files, Python
caches, lock files, staging directories and validation reports from the repeat-sync file
comparison; keep task reports outside managed Skill directories. Do not exclude managed
files merely because their extension resembles a report. Neither baseline replaces the
five-target check or the two installed validators.

If the required pre-apply evidence was not collected, report only the current consistency
that was actually verified. Do not retroactively claim outside-byte preservation or
unchanged timestamps. Unexpected differences require inspection, not a hand-edit of
generated outputs or an automatic retry that hides the evidence.

### Hash field interpretation

Only compare hashes with the same scope and algorithm:

| Field | Scope and comparison |
|---|---|
| `templateSha256` | Manifest plus all three templates, including their relative paths and bytes. Compare with the same selected source's template set; this is not a hash of every distributed reference or script. |
| Rule target `beforeSha256`, `expectedSha256`, `afterSha256` | The complete rule file, including business content outside the managed block. Compare before/after with the expected candidate for that same target, not with another rule file. |
| Rule record `normalizedManagedSha256` | Only the managed block after newline normalization, removal of the `generatedAt` value and trailing-whitespace trimming. Compare normalized blocks, not raw whole-file bytes. |
| Skill `contentSha256` (also the Skill target's content hashes) | Filtered relative paths and file bytes under that Skill directory. The installer excludes `.upilot-install.json`, Unity `.meta` files and Python caches; compare using that same filter and ordering. |

A template hash alone cannot establish that all distributed resources are current.
Different business rules can give two rule files different full-file hashes without
managed-block drift. Equal Skill content hashes do not validate their excluded metadata:
check each installation's schema/version, `templateSha256` and `renderContext` separately,
including the intended project, endpoints and package version.

For template/Markdown-only changes, use generation and installation checks rather than
triggering Unity compilation. If C# or assembly code also changes, follow the separate
correlated Unity compile workflow and narrow targeted tests, not a default full suite.

The complete tools use the connected Unity project's actual installed UPM package,
not the Python Server/EXE's bundled templates. A Bridge without the new commands must
be upgraded; the Server never falls back to directly rewriting project files.
Legacy `unity_agent_rules_check/install` remains limited to project `AGENTS.md`.

When MCP is unavailable, report why and use the existing CLI with an explicit package
source and Unity project, preserving the same generation and verification sequence:

```bash
python scripts/install_upilot.py --integrations-only \
  --unity-project <PROJECT_ROOT> --upilot-dir <UPILOT_SOURCE> --dry-run --json
```

Remove `--dry-run` to apply. This mode synchronizes all five project targets, without
changing UPM dependencies, Python environments or MCP client registrations. It cannot
be combined with `--skill-only` or user-level installation options. Normal installation
also synchronizes Agent rules; `--skill-only` continues to install only Skills.
For the final offline complete check, repeat the command with `--dry-run --json` and
require all five targets to be `current`. The CLI is an explicit maintenance fallback,
not an automatic Server-side file-write fallback.
Port precedence is `--http-port`, project `.upilot/config.json` (`mcp.httpPort`), then
the template manifest default. Package version comes from the selected source's
`package.json`. Source-generated Skills always use the default source profile.

Synchronization holds `.upilot/agent-integrations.lock` using an OS byte-range lock
shared by C# and Python. Busy returns without overwriting anything. Previews never
create the lock or other files. Rule metadata is stored in `.upilot/agent-integrations.json`;
Skill metadata remains `.upilot-install.json` schema v2, with v1 upgrade compatibility.
Generation timestamps alone do not cause rule rewrites or backups.

Each target is a separate transaction: validate all sources first, build a candidate
under `.upilot/agent-integration-staging/`, verify required backups, recheck the target
hash, replace, verify content and metadata, then remove rollback material. On failure,
restore that target and continue independent targets. Recovery material is retained if
rollback fails. Duplicate, one-sided or reversed managed markers and symlink/junction
paths fail closed. Results include per-target reasons, hashes, backup paths and errors;
partial failure is never reported as complete success. Historical backups are not
automatically deleted. Missing source, malformed markers, unsafe paths, busy locks,
backup failure, or verification failure leave the affected work incomplete: report
the exact targets and next action, never patch outputs to bypass the failure or
require the user to discover an omitted synchronization step.

There is no background template file watcher. Automatic maintenance here means that
the Agent completes this sequence inside the authorized task; an isolated manual save
does not schedule background generation or project updates.
