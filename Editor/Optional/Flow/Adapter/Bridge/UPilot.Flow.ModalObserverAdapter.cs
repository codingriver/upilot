using System;
using CodingRiver.UPilot.Flow;
using UnityEditor;

namespace CodingRiver.UPilot
{
    [InitializeOnLoad]
    internal sealed class FlowModalObserverAdapter : IFlowModalObserver
    {
        private readonly UPilotModalObserver.Watch _watch;
        static FlowModalObserverAdapter() =>
            UPilotFlowModalWatchdog.CreateObserver = (window, budget) => new FlowModalObserverAdapter(window, budget);

        private FlowModalObserverAdapter(EditorWindow window, int budget) =>
            _watch = UPilotModalObserver.Arm("flow-modal-" + Guid.NewGuid().ToString("N"),
                window, escapeGenericMenu: window != null, budgetMs: budget);

        public void Recover() => _watch.RecoverGenericMenu();
        public bool ActionPosted => _watch.Snapshot().actionPosted;
        public string Error => _watch.Snapshot().error;
        public void Dispose() => _watch.Complete(null);
    }
}
