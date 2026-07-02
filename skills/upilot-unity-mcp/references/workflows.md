# Workflows

Use these flows when a user asks for a Unity task and the upilot MCP server is available.

## Health Check

1. Call `unity_mcp_status`.
2. Confirm `connected: true` and verify `paths.unityProjectAbsolute`.
3. Call `unity_ensure_ready`.
4. If Unity is disconnected, ask the user to open the target Unity project and start or reconnect the upilot bridge.

## Fix Compile Errors

1. Call `unity_compile_errors`.
2. Read the referenced files from disk.
3. Patch the smallest code surface that explains the compiler errors.
4. Call `unity_sync_after_disk_write`.
5. Call `unity_safe_compile_and_wait`.
6. Re-read `unity_compile_errors` and `unity_console_tail_logs`.

## Edit Scripts Safely

1. Read existing scripts from disk before changing them.
2. Prefer local project patterns over new abstractions.
3. After edits, call `unity_sync_after_disk_write`.
4. Call `unity_safe_compile_and_wait` for C# changes.
5. Use `unity_console_tail_logs` to catch runtime/editor warnings.

## Modify Scene Objects

1. Call `unity_mcp_status` and verify the target project.
2. Call `unity_scene_list` or `unity_scene_open` when scene context matters.
3. Use `unity_gameobject_find` before modifying objects by name.
4. Use component tools for component add/get/modify/remove.
5. Save the scene only when the user requested a persistent scene change.

## Run Tests

1. Call `unity_ensure_ready`.
2. Use `unity_test_list` when test names or filters are unclear.
3. Call `unity_test_run`.
4. Poll `unity_test_results`.
5. Read Console logs when failures mention setup, domain reload, or missing assets.

## Screenshot And Visual Check

1. Use `unity_screenshot_editor_window` for EditorWindow UI.
2. Use `unity_screenshot_scene_view` for Scene view framing.
3. Use `unity_screenshot_game_view` for Game view output.
4. Re-run screenshots after layout or visual changes.

## UIFlow YAML Automation

1. Confirm Unity 6+ and `UPILOT_ENABLE_UIFLOW`.
2. Use UIFlow only for EditorWindow automation.
3. Prefer `unity_uiflow_run_file` for one spec and `unity_uiflow_run_suite` for a named suite.
4. Keep batch runs small; split large suites.
5. Use result artifacts and failure screenshots to diagnose selector or timing failures.

## Multiple Unity Projects

1. Always verify `paths.unityProjectAbsolute`.
2. If the wrong project is connected, stop and ask the user to reconnect the intended Unity Editor.
3. Use distinct WebSocket ports such as `8765`, `8766`, and `8767` when several projects run at once.
