# MonoHook（核心库）

本目录为 [MonoHook](https://github.com/Misaka-Mikoto-Tech/MonoHook) 的核心源码拷贝，用于在编辑器内对托管方法做运行时 Hook（MIT License，见仓库根目录分发说明）。

- **不安全代码**：由 `UnityPilot.Editor.asmdef` 的 `allowUnsafeCode` 启用。
- **原生插件**：`Plugins/` 含 macOS 用 `libMonoHookUtils_OSX.dylib` 与 `Utils.cpp`（与上游一致）。
- **用法**：在任意 `Editor` 脚本中引用 `MonoHook` 命名空间（如 `MethodHook`），勿与业务代码强耦合；升级 Unity 小版本后请自行验证 Hook 目标方法是否仍适用。
