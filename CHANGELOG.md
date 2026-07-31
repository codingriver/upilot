# Changelog

## 0.3.11

- Add a non-blocking main-window release reminder with per-version skip support.
- Surface update progress in the main window and open the update center directly into the active update state.

## 0.3.10

- Treat main/source installs as Python-only development builds.
- Publish managed MCP server binaries only from tagged release builds.

## 0.3.7

- Add release CI validation so tag, UPM package, and MCP server versions must match before publishing release assets.
- Keep update manifests aligned across UPilot package and managed MCP service releases.

## 0.3.5

- Stop the MCP service before UPilot package updates initiated from either the update center or Unity Package Manager, then restore it after package registration and assembly reload.
- Preserve update restart intent across domain reloads while avoiding automatic service startup during first installation.
- Prefer the current source package version over stale installed Python distribution metadata when reporting the MCP server version.

## 0.3.3

- Added a unified update center that clearly separates release and Main builds, package updates, and managed MCP service updates.
- Corrected local-development and Python runtime update guidance so bundled services are not compared with remote managed builds.
- Refined the main UPilot window with narrower responsive layout, clearer status hierarchy, and aligned Agent configuration controls.

## 0.3.2

- Refined the UPilot main window into a denser status dashboard with compact runtime details and table-style Agent configuration controls.
- Improved Agent MCP and Skill/rule consistency checks, update guidance, and preferences handling.
- Expanded long-running task operations, compile tooling, and related automated coverage.
- Constrained the Python MCP SDK to the compatible 1.x series so standalone server builds remain runnable.

## 0.2.0

- Added persistent Unity Console capture sessions with JSONL output, incremental reads, rotation, summaries, SHA256 verification, session listing, and confirm-token cleanup.
- Renamed the core product, C# namespaces, assemblies, menus, and documentation to UPilot.
- Added a stable tool registry, schema-v2 MCP responses, structured errors, cache freshness, operation timing, and async task tools.
- Standardized Streamable HTTP on port 8011 and kept WebSocket ports internal to the Unity Bridge.
- Added project configuration at `.upilot/config.json` and client configuration diagnostics.
- Made UPilot Flow optional and disabled by default, with Unity 6 and define constraints.
- Added UPilot Flow schema version 2, validation, dry-run migration, action descriptors, and migrated samples.
- Updated Agent rules and the UPilot skill to use capability discovery and phase-based acceptance.
