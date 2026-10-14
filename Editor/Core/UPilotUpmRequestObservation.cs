// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace CodingRiver.UPilot
{
    internal sealed class UPilotUpmRequestException : Exception
    {
        internal readonly string Code;
        internal UPilotUpmRequestException(string code, string message) : base(message) { Code = code; }
    }

    // Owns only explicitly requested UPM observations. Reading diagnostics never starts UPM.
    [InitializeOnLoad]
    internal sealed class UPilotUpmRequestObservation
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<UPilotUpmRequestObservation> Active = new HashSet<UPilotUpmRequestObservation>();
        private static UPilotUpmRequestObservation _last;
        private readonly object _owner;
        private readonly CancellationToken _token;
        private readonly string _lifetime;
        private readonly Func<long> _clock;
        private Action _start, _success;
        private Action<Exception> _failure;
        private Func<bool> _isCompleted;
        private Func<Exception> _requestError;
        private bool _ended, _subscribed;
        private long _nextCheckAt;
        internal readonly string Id, Kind;
        internal string Status = "queued", EndReason = "";
        internal int TimeoutSeconds, CheckIntervalMs;
        internal long StartedAtUtcMs, DeadlineUtcMs, EndedAtUtcMs;
        internal bool IsEnded => _ended;
        internal bool IsSubscribed => _subscribed;

        static UPilotUpmRequestObservation()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () => ResetAll("domain_reload");
            EditorApplication.quitting += () => ResetAll("editor_quitting");
        }

        private UPilotUpmRequestObservation(object owner, string id, string kind, Action start,
            Func<bool> completed, Func<Exception> requestError, Action success, Action<Exception> failure,
            CancellationToken token, Func<long> clock)
        {
            _owner = owner; Id = id; Kind = kind; _start = start; _isCompleted = completed;
            _requestError = requestError; _success = success; _failure = failure; _token = token;
            _lifetime = UPilotServiceLifetime.Id;
            _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            lock (Gate) Active.Add(this);
        }

        internal static UPilotUpmRequestObservation Create(object owner, string id, string kind, Action start,
            Func<bool> completed, Func<Exception> requestError, Action success, Action<Exception> failure,
            CancellationToken token = default, Func<long> clock = null) =>
            new UPilotUpmRequestObservation(owner, id, kind, start, completed, requestError, success, failure, token, clock);

        internal static UPilotUpmRequestObservation[] Diagnostics()
        {
            lock (Gate) return Active.Count > 0 ? Active.ToArray() : _last == null ? Array.Empty<UPilotUpmRequestObservation>() : new[] { _last };
        }

        internal static void ResetOwner(object owner, string reason)
        {
            UPilotUpmRequestObservation[] observations;
            lock (Gate) observations = Active.Where(x => ReferenceEquals(x._owner, owner)).ToArray();
            foreach (var observation in observations) observation.Cancel(reason);
        }

        internal static void ResetAll(string reason)
        {
            UPilotUpmRequestObservation[] observations;
            lock (Gate) observations = Active.ToArray();
            foreach (var observation in observations) observation.Cancel(reason);
        }

        // Start runs on the Editor thread; a queued observation is already owned for Reset.
        internal void Start()
        {
            if (_ended || Status != "queued") return;
            if (_token.IsCancellationRequested || _lifetime != UPilotServiceLifetime.Id) { Cancel("canceled_before_start"); return; }
            TimeoutSeconds = UPilotTimingSettings.UpmRequestObservationTimeoutSeconds;
            CheckIntervalMs = UPilotTimingSettings.UpmCompletionCheckIntervalMs;
            StartedAtUtcMs = _clock(); DeadlineUtcMs = StartedAtUtcMs + TimeoutSeconds * 1000L;
            Status = "observing";
            try
            {
                _start();
                if (_ended) return;
                EditorApplication.update += Tick;
                _subscribed = true;
                Tick();
            }
            catch (Exception ex) { Finish(ex, "start_failed"); }
        }

        internal void Tick()
        {
            if (_ended) return;
            if (_token.IsCancellationRequested || _lifetime != UPilotServiceLifetime.Id) { Cancel("canceled_or_reset"); return; }
            long now = _clock();
            if (now >= DeadlineUtcMs)
            {
                Finish(new UPilotUpmRequestException("UPM_OBSERVATION_TIMEOUT",
                    $"UPM 请求观察超时（{TimeoutSeconds} 秒）；本地观察已结束，UPM 最终结果未知，未物理取消或重发请求。"), "observation_timeout");
                return;
            }
            if (now < _nextCheckAt) return;
            _nextCheckAt = now + CheckIntervalMs;
            try
            {
                if (!_isCompleted()) return;
                var error = _requestError();
                Finish(error, error == null ? "completed" : "upm_failed");
            }
            catch (Exception ex) { Finish(ex, "observation_failed"); }
        }

        internal void CompleteFromRegistration() => Finish(null, "package_registered");

        internal void Cancel(string reason) => Finish(new OperationCanceledException(reason), reason);

        private void Finish(Exception error, string reason)
        {
            if (_ended) return;
            _ended = true;
            if (_subscribed) EditorApplication.update -= Tick;
            _subscribed = false;
            Status = error == null ? "succeeded" : error is OperationCanceledException ? "stopped" : "failed";
            EndReason = reason; EndedAtUtcMs = _clock();
            var success = _success; var failure = _failure;
            _start = null; _isCompleted = null; _requestError = null; _success = null; _failure = null;
            lock (Gate) { Active.Remove(this); _last = this; }
            try { if (error == null) success(); else failure(error); }
            catch (Exception ex)
            {
                Status = "failed"; EndReason = "completion_callback_failed: " + ex.Message;
                if (error == null) { try { failure(ex); } catch { } }
            }
        }

        internal static Task<TResult> RunAsync<TRequest, TResult>(UPilotBridge bridge, string id, string kind,
            Func<TRequest> start, Func<TRequest, TResult> result, CancellationToken token) where TRequest : Request
        {
            var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            TRequest request = null;
            var observation = Create(bridge, id, kind,
                () => { request = start(); if (request == null) throw new InvalidOperationException("UPM returned no request."); UPilotOperationTracker.Instance.GetContext(id)?.Step("等待包管理器完成"); },
                () => request.IsCompleted,
                () => request.Status == StatusCode.Failure ? new UPilotUpmRequestException("UPM_REQUEST_FAILED", request.Error?.message ?? "Unknown UPM error") : null,
                () => { try { tcs.TrySetResult(result(request)); } finally { request = null; } },
                ex => { request = null; if (ex is OperationCanceledException) tcs.TrySetCanceled(); else tcs.TrySetException(ex); }, token);
            try { bridge.EnqueueTracked(id, observation.Start); }
            catch (Exception ex) { observation.Finish(ex, "enqueue_failed"); }
            return tcs.Task;
        }
    }
}
