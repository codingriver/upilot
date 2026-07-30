# Changelog

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
