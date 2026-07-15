# UPilot 0.2 Migration

## Core

- Keep the UPM package ID `io.github.codingriver.upilot`.
- Update C# namespaces from `codingriver.upilot` to `CodingRiver.UPilot`.
- Update assemblies from `Upilot.Editor` to `UPilot.Editor`.
- Configure MCP clients with `http://127.0.0.1:8011/mcp`; do not configure the internal WebSocket port.
- Replace project-local endpoint overrides with `.upilot/config.json`.
- Restart the MCP client after upgrading so the v0.2 tool list is injected.

## Optional Flow

The old UIFlow feature is now UPilot Flow and is disabled by default.

- `codingriver.upilot.UIFlow` becomes `CodingRiver.UPilot.Flow`.
- `UIFlow` and `Upilot.UIFlowBridge` assemblies become `UPilot.Flow` and `UPilot.FlowBridge`.
- `UPILOT_ENABLE_UIFLOW` becomes `UPILOT_ENABLE_FLOW`.
- `.uiflow.json` becomes `.upilot-flow.json`.
- `unity_uiflow_*` becomes `unity_upilot_flow_*`.
- `uiFlow.*` Bridge commands become `upilot_flow.*`.
- `Reports/upilot/UIFlowMcp` becomes `Reports/UPilot/Flow`.

Legacy YAML without `schemaVersion` is read as version 1. Run `unity_upilot_flow_migrate` with `dryRun=true`, review invalid actions and target files, then rerun with `dryRun=false`. Version 2 YAML starts with `schemaVersion: 2`.

UPilot Flow requires Unity 6, its optional packages, and `UPILOT_ENABLE_FLOW`. Unity 2022 projects continue to use all UPilot core tools with Flow disabled.
