# Tool Boundaries

Use this page when choosing tools or after a tool fails because the request is outside its scope.

## General Rules

- Always call `unity_mcp_status` first and verify `paths.unityProjectAbsolute` before writes.
- Prefer the narrowest dedicated tool over generic input, batch, reflection, or serialized-data tools.
- For destructive actions, first read/list/get the target and confirm it is the intended object, asset, package, or scene.
- After disk writes from the IDE or script tools, call `unity_sync_after_disk_write` once for the whole batch, then compile/wait.
- If a tool reports a boundary error, do not retry with random variants. Switch tool class or ask for a helper/API.

## Destructive Or Persistent Tools

These can permanently change scene, asset, package, or script state:

- `unity_asset_delete`, `unity_asset_move`, `unity_asset_modify_data`
- `unity_script_create`, `unity_script_update`, `unity_script_delete`
- `unity_package_add`, `unity_package_remove`
- `unity_scene_save`, `unity_scene_load(mode="single")`, `unity_scene_unload`
- `unity_gameobject_delete`, `unity_component_remove`
- `unity_batch_execute` when it contains any of the above

Before using them, verify the target with the matching read/list/find/get tool. For scene-only changes, remember they persist only after scene or prefab save. For disk/package/script changes, expect Unity import/compile side effects.

## Input And Window Tools

- `unity_mouse_event`, `unity_keyboard_event`, and `unity_drag_drop` interact with the real Unity UI. They depend on focus, layout, docking, and coordinates.
- Prefer semantic tools first: scene, asset, GameObject, component, package, menu, window, or UIFlow tools.
- Prefer `elementName` or listed window metadata over raw coordinates.
- Do not use keyboard text entry unless the target window and focused field are known.

## Reflection Tools

- `unity_reflection_call` calls existing compiled methods. Use it for stable business/test helper APIs.
- `reflection_eval` evaluates one bounded expression only. It is not a script runner.
- If logic needs local variables, loops, branches, lambda/LINQ, helper methods, arbitrary object construction, or dynamic compilation, do not keep trying `reflection_eval`; add a compiled helper and call it.

## Batch And Task Tools

- `unity_task_execute` is for long-running or potentially stuck idempotent calls. Avoid wrapping non-idempotent destructive operations with retries.
- `unity_batch_execute` is for repeated or grouped calls. Use `stopOnError=true` when later operations depend on earlier results.
- Do not hide risky operations inside batches unless the user has approved the whole batch.

## UIFlow And E2E

- `unity_uiflow_*` tools only run YAML-driven Unity EditorWindow automation. They are not for Game View or runtime UI tests.
- UIFlow requires the package/runtime pieces and an enabled `UPILOT_ENABLE_UIFLOW` setup where applicable.
- `unity_editor_e2e_run` runs an existing YAML spec from disk and writes artifacts. It is not a general-purpose UI action tool.

## Screenshots

- Screenshot tools are observational except `unity_screenshot_save`, which writes a PNG to disk.
- `unity_screenshot_save` defaults to a project-local output path. Only use `allowOutsideProject=true` when the user explicitly wants an external path.
