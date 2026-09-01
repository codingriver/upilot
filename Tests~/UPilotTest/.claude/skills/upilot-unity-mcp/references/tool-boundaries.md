# Tool Boundaries

- Server registration, client injection, and successful invocation are separate capability states.
- Query capabilities before declaring a missing client-side tool unavailable.
- Read/list/find/get before destructive or persistent operations.
- Do not place destructive calls inside automatic retries.
- `unity_task_execute` retries only idempotent, non-destructive operations.
- `unity_reflection_call` invokes existing compiled methods.
- `reflection_eval` accepts one bounded expression; no declarations, loops, branches, lambdas, async code, helper definitions, or dynamic compilation.
- Mouse, keyboard, and drag tools affect the real focused UI and are layout-sensitive.
- `unity_screenshot_save` writes a PNG; other screenshot tools are observational.
- Manual Unity YAML editing is a last resort and must preserve GUID/fileID integrity.
