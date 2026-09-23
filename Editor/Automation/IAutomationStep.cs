namespace CodingRiver.UPilot.Automation
{
    /// <summary>
    /// Main-thread, nonblocking lifecycle. Validate is read-only and checks syntax/static constraints,
    /// not conditions established by earlier steps. Execute is never replayed after reload.
    /// Cleanup may be called repeatedly and must only succeed after owned work/resources are released.
    /// </summary>
    public interface IAutomationStep
    {
        string Validate(string runId, string instanceId, string contextJson, string arguments);
        void Execute(string runId, string instanceId, string contextJson, string arguments);
        string Poll(string runId, string instanceId, string contextJson, string arguments);
        string GetError(string runId, string instanceId, string contextJson, string errorCode, string arguments);
        void Cancel(string runId, string instanceId, string contextJson, string arguments);
        string Cleanup(string runId, string instanceId, string contextJson, string arguments);
        string Restore(string runId, string instanceId, string contextJson, string arguments);
    }
}
