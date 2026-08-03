# UPilot Flow

Load this reference only for explicit UPilot Flow requests.

- UPilot Flow is an optional module inside the UPilot package.
- It requires Unity 6+, required optional packages, and `UPILOT_ENABLE_FLOW`.
- It is disabled by default and unavailable on Unity 2022.
- Enabling or disabling it requires explicit user confirmation because package dependencies and scripting defines may change.
- After enabling it, restart the MCP client so `unity_upilot_flow_*` tools are injected.
- Use Flow only for YAML-driven Unity EditorWindow automation, not Game View or runtime UI.
- New YAML uses `schemaVersion: 2`.
- Migrate legacy UIFlow YAML with `unity_upilot_flow_migrate`; keep `dryRun=true` for the first pass.
- Validate migrated YAML with `unity_upilot_flow_validate` before execution.
- Treat report JSON as authoritative; Markdown and headed UI are projections.
