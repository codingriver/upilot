# UPilot release process

Use the **Prepare UPilot Release** GitHub Action as the only release entry point.

## Why

Unity Package Manager reads `package.json` from the exact Git tag revision. If a tag is
created before `package.json` is bumped, a later build step cannot repair that UPM
version for users installing `https://github.com/codingriver/upilot.git#vX.Y.Z`.

The release flow therefore treats the requested version as the source of truth, writes it
into both release metadata files, commits those files, then creates the tag from that
version-synchronized commit.

## Flow

1. Run **Prepare UPilot Release** with `version` set to `X.Y.Z` or `vX.Y.Z`.
2. The workflow updates:
   - `package.json`
   - `upilotserver~/pyproject.toml`
3. The workflow commits the version bump to `main`.
4. The workflow creates and pushes tag `vX.Y.Z`.
5. The tag push triggers **Build UPilot MCP Server EXE**.
6. The build workflow validates the tag commit still contains matching UPM and Python
   versions, embeds the tag version into the standalone server, and publishes release
   assets plus `manifest.json`.

## Bad tag recovery

If a tag was created from a commit with stale versions, do not reuse it as-is. Delete the
bad local/remote tag intentionally, then rerun **Prepare UPilot Release** for the same
version or choose a new patch version.
