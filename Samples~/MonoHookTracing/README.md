# MonoHook Tracing Custom Provider

Import this sample from Package Manager, then open `UPilot > Advanced > MonoHook`.

The sample assembly references only `UPilot.MonoHook.Tracing.Contracts.Editor`. The `Sample Custom / Sample SetValue` point is discovered from `UPilotMonoHookPointAttribute`. Like every MonoHook Tracing point, it is disabled by default and is only installed after you enable it and click **Apply**.

Call `MonoHookTracingSampleTarget.SetValue(int)` to produce a `sample.custom.setValue` event while the point is installed. Enable the point's stack checkbox if this custom event should include a sampled call stack.
