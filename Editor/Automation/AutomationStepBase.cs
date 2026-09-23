namespace CodingRiver.UPilot.Automation
{
    /// <summary>Optional conveniences only; enforcement belongs to the executor.</summary>
    public abstract class AutomationStepBase : IAutomationStep
    {
        private string _result = "{\"status\":\"Running\"}";
        private string _error;
        protected bool IsRunning { get; private set; } = true;
        public virtual string Validate(string runId, string instanceId, string contextJson, string arguments) => ValidationOk();
        public abstract void Execute(string runId, string instanceId, string contextJson, string arguments);
        public virtual string Poll(string runId, string instanceId, string contextJson, string arguments) => _result;
        public virtual string GetError(string runId, string instanceId, string contextJson, string errorCode, string arguments) =>
            _error ?? AutomationStepJsonCodec.ErrorJson(errorCode);
        public virtual void Cancel(string runId, string instanceId, string contextJson, string arguments) { }
        public virtual string Cleanup(string runId, string instanceId, string contextJson, string arguments) => ResultJson("Succeeded");
        public virtual string Restore(string runId, string instanceId, string contextJson, string arguments) => "{\"status\":\"Unsupported\"}";
        protected void Succeed(bool warnings = false)
        { IsRunning = false; _result = ResultJson(warnings ? "SucceededWithWarnings" : "Succeeded"); }
        protected void Skip() { IsRunning = false; _result = ResultJson("Skipped"); }
        protected void Fail(string code, string message, string diagnostic = "")
        {
            var result = ResultJson("Failed", code);
            _error = AutomationStepJsonCodec.ErrorJson(message, diagnostic);
            IsRunning = false; _result = result;
        }
        protected static string ValidationOk() => "{\"ok\":true}";
        protected static string ValidationError(string code, string message) =>
            AutomationStepJsonCodec.ValidationErrorJson(code, message);
        protected static string ResultJson(string status, string errorCode = "") =>
            AutomationStepJsonCodec.ResultJson(status, errorCode);
        protected static void SaveCheckpoint(string runId, string instanceId, string checkpointJson) =>
            UPilotAutomationStepService.SaveCheckpoint(runId, instanceId, checkpointJson);
        protected static void SaveSharedValue(string runId, string instanceId, string key, string valueJson) =>
            UPilotAutomationStepService.SaveSharedValue(runId, instanceId, key, valueJson);
        protected static void RegisterArtifact(string runId, string instanceId, string kind, string path) =>
            UPilotAutomationStepService.RegisterArtifact(runId, instanceId, kind, path);
        protected static string BeginSnapshotJson(string runId, string instanceId, string evidenceKey, string arguments) =>
            UPilotAutomationStepService.BeginSnapshotJson(runId, instanceId, evidenceKey, arguments);
        protected static string PollSnapshotJson(string runId, string instanceId, string evidenceKey) =>
            UPilotAutomationStepService.PollSnapshotJson(runId, instanceId, evidenceKey);
        protected static string CancelSnapshotJson(string runId, string instanceId, string evidenceKey) =>
            UPilotAutomationStepService.CancelSnapshotJson(runId, instanceId, evidenceKey);
        protected static string SnapshotErrorJson(string runId, string instanceId, string evidenceKey) =>
            UPilotAutomationStepService.SnapshotErrorJson(runId, instanceId, evidenceKey);
    }
}
