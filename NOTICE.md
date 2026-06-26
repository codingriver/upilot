# Third-Party Notices

This project embeds or distributes selected third-party components. The main
project license is in `LICENSE.md`. Third-party components keep their original
licenses.

## MonoHook

- Source: <https://github.com/Misaka-Mikoto-Tech/MonoHook>
- Location: `Editor/Plugins/MonoHook/`
- License: MIT
- Use in this project: editor-only managed method interception for Unity Editor
  IMGUI and automation support.
- Notes: MonoHook requires C# `unsafe` code and uses the `allowUnsafeCode`
  option in the relevant `.asmdef` files. The embedded native macOS utility is
  located under `Editor/Plugins/MonoHook/Plugins/`.

## YamlDotNet

- Source: <https://github.com/aaubry/YamlDotNet>
- Location: `Editor/Plugins/YamlDotNet.dll` and `Editor/Plugins/YamlDotNet.xml`
- License: MIT
- Use in this project: YAML parsing for UIFlow test cases and reports.

## Python Dependencies

Python dependencies for the MCP server are declared in
`unitypilot~/pyproject.toml` and `unitypilot~/requirements.txt`. They are not
vendored as source in this Unity package and retain their respective upstream
licenses.
