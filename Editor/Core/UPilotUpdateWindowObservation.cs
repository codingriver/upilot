// SPDX-License-Identifier: MIT
using System;

namespace CodingRiver.UPilot
{
    // Window-owned scheduling only. It never starts or stops the update operation.
    internal sealed class UPilotUpdateWindowObservation
    {
        internal bool Enabled { get; private set; }
        internal long Generation { get; private set; }
        internal long LastReadAt, NextReadAt, RefreshDeadline;
        internal string PendingOperation = "", ConsumedOperation = "", Lifetime = "";
        internal int FrozenDelaySeconds;
        private bool _dirty;
        private string _runningOperation = "";
        internal void Open(string lifetime) { Close(); Enabled = true; Lifetime = lifetime; _dirty = true; }
        internal void Close() { Enabled = false; ++Generation; if (PendingOperation.Length > 0) ConsumedOperation = PendingOperation; CancelPending(); }
        internal void DiscardCompletion(string id) { ConsumedOperation = id ?? ""; CancelPending(); }
        internal void CancelPending() { PendingOperation = ""; RefreshDeadline = 0; }
        internal void Dirty() => _dirty = true;
        internal void AdjustInterval(int milliseconds) => NextReadAt = LastReadAt + milliseconds;
        internal bool ShouldRead(long now, int interval)
        {
            if (!Enabled || (!_dirty && now < NextReadAt)) return false;
            _dirty = false; LastReadAt = now; NextReadAt = now + interval; return true;
        }
        internal void ObserveOperation(string id, bool running)
        {
            if (running && id != _runningOperation) { CancelPending(); ++Generation; }
            _runningOperation = running ? id : "";
        }
        internal bool Schedule(string id, long now, int seconds)
        {
            if (!Enabled || string.IsNullOrEmpty(id) || id == ConsumedOperation || id == PendingOperation) return false;
            PendingOperation = id; FrozenDelaySeconds = seconds; RefreshDeadline = now + seconds * 1000L; return true;
        }
        internal bool ConsumeDue(long now)
        {
            if (!Enabled || PendingOperation.Length == 0 || now < RefreshDeadline) return false;
            ConsumedOperation = PendingOperation; CancelPending(); return true;
        }
        internal long BeginCheck()
        {
            if (PendingOperation.Length > 0) ConsumedOperation = PendingOperation;
            CancelPending(); return ++Generation;
        }
        internal bool IsCurrent(long generation, string lifetime) => Enabled && Generation == generation && Lifetime == lifetime;
    }
}
