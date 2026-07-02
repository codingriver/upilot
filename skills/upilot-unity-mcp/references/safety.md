# Safety And Recovery

Use these rules before irreversible or environment-sensitive actions.

## Destructive Operations

Confirm the target and project before:

- `unity_asset_delete`
- `unity_component_remove`
- `unity_gameobject_delete`
- `unity_package_remove`
- scene saves or scene unloads
- batch operations that include any destructive step

When possible, read the target first with a corresponding find/get/list tool.

## Project Identity

Always verify `paths.unityProjectAbsolute` from `unity_mcp_status` when:

- more than one Unity project may be open
- the user references a specific project path
- a task writes files, assets, packages, or scenes

Stop if the connected Unity project is not the intended project.

## Compile And PlayMode

- Avoid triggering compilation while Unity is in PlayMode unless the user explicitly asks for a PlayMode workflow.
- Use `unity_compile_wait` or `unity_safe_compile_and_wait` after C# edits.
- Read `unity_compile_errors` before changing files for a compile fix.

## Timeouts

If a tool may hang or Unity is busy, use `unity_task_execute` to run the call with a watchdog.

If a direct tool call times out:

1. Call `unity_mcp_status`.
2. Check connection and compile state.
3. Retry once through `unity_task_execute` when the operation is idempotent.
4. Stop and report the state if Unity remains disconnected or busy.

## UIFlow Unavailable

If a UIFlow tool returns `UIFLOW_UNAVAILABLE`:

1. Check Unity version.
2. Check the `UPILOT_ENABLE_UIFLOW` scripting define.
3. Continue with core upilot tools if the user does not specifically require UIFlow.

## Dynamic Code

Prefer `unity_reflection_call` and `reflection_eval` before `unity_roslyn_execute`.

Do not use dynamic code for destructive changes when a dedicated upilot tool exists.
