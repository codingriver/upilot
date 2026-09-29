// -----------------------------------------------------------------------

// UPilot Editor — M26 editor.delay (main-thread wall-clock delay for E2E)

// SPDX-License-Identifier: MIT

// -----------------------------------------------------------------------



using System.Threading;

using System.Threading.Tasks;

using UnityEditor;

using UnityEngine;



namespace CodingRiver.UPilot

{

    public sealed class UPilotEditorDelayService

    {

        private readonly UPilotBridge _bridge;
        private readonly System.Collections.Generic.Dictionary<TaskCompletionSource<bool>, EditorApplication.CallbackFunction> _pending = new();

        internal void ResetActive()
        {
            foreach (var entry in _pending)
            {
                EditorApplication.update -= entry.Value;
                entry.Key.TrySetCanceled();
            }
            _pending.Clear();
        }



        public UPilotEditorDelayService(UPilotBridge bridge)

        {

            _bridge = bridge;

            Logger.Log("[EditorDelay] EditorDelayService 初始化");

        }



        public void RegisterCommands()

        {

            _bridge.Router.Register("editor.delay", HandleEditorDelayAsync);

        }



        private async Task HandleEditorDelayAsync(string id, string json, CancellationToken token)

        {

            Logger.Log("EditorDelay", $"开始延迟 id={id}");

            var command = JsonUtility.FromJson<EditorDelayMessage>(json);

            var delayMs = command?.payload?.delayMs ?? 0;

            if (delayMs < 0) delayMs = 0;

            if (delayMs > 300000) delayMs = 300000;



            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            double start = 0d;
            string lifetime = UPilotServiceLifetime.Id;



            void Tick()

            {

                if (tcs.Task.IsCompleted || lifetime != UPilotServiceLifetime.Id
                    || (EditorApplication.timeSinceStartup - start) * 1000.0 >= delayMs)

                {

                    EditorApplication.update -= Tick;
                    _pending.Remove(tcs);

                    if (lifetime != UPilotServiceLifetime.Id) tcs.TrySetCanceled();
                    else tcs.TrySetResult(true);

                }

            }



            _bridge.EnqueueTracked(id, () =>

            {

                if (tcs.Task.IsCompleted || lifetime != UPilotServiceLifetime.Id)
                { tcs.TrySetCanceled(); return; }
                start = EditorApplication.timeSinceStartup;

                if (delayMs <= 0)

                {

                    tcs.TrySetResult(true);

                    return;

                }



                _pending[tcs] = Tick;
                EditorApplication.update += Tick;

                Tick();

            });



            using var registration = token.Register(() => tcs.TrySetCanceled());
            await tcs.Task;
            if (lifetime != UPilotServiceLifetime.Id) return;

            await _bridge.SendResultAsync(

                id,

                "editor.delay",

                new GenericOkPayload { ok = true, state = $"delayed_{delayMs}ms" },

                token);

        }

    }

}
