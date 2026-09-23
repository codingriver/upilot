using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CodingRiver.UPilot.Automation
{
    /// <summary>Main-thread polled collector over a fixed half-open range. File scans run asynchronously.</summary>
    public sealed class AutomationConsoleCollector : IDisposable
    {
        private readonly string _session;
        private readonly long _from, _to, _deadline;
        private readonly CancellationTokenSource _cancel = new();
        private Task<ConsoleCaptureReadResult> _read;
        private string _continuation;
        private long _next;
        private bool _diskReady;
        public bool Complete { get; private set; }
        public List<ConsoleCaptureRecord> Records { get; } = new();
        public AutomationLogEvidence Evidence { get; }
        public AutomationConsoleCollector(string session, long fromInclusive, long toExclusive, double timeoutSeconds = 30)
        {
            if (string.IsNullOrEmpty(session) || fromInclusive < 0 || toExclusive < fromInclusive
                || !AutomationStepJson.Finite(timeoutSeconds) || timeoutSeconds <= 0)
                throw new ArgumentException("Invalid capture identity, range or timeout.");
            _session = session; _from = fromInclusive; _to = toExclusive; _next = fromInclusive;
            _deadline = AutomationStepJson.Now + (long)(timeoutSeconds * 1000);
            Evidence = new AutomationLogEvidence { expectedSessionId = session, pagesComplete = false };
        }
        public void Poll()
        {
            if (Complete) return;
            if (AutomationStepJson.Now >= _deadline)
            { Evidence.timedOut = true; Finish("Console evidence deadline exceeded."); return; }
            var status = UPilotConsoleCaptureApi.Status(_session);
            if (!status.ok || status.session == null) { Evidence.readSucceeded = false; Finish(status.error); return; }
            Evidence.actualSessionId = status.session.sessionId;
            Evidence.identityMatches = Evidence.actualSessionId == _session;
            Evidence.lostRecordCount = status.session.droppedCount;
            Evidence.writeSucceeded = string.IsNullOrEmpty(status.session.lastError);
            if (!Evidence.identityMatches || !Evidence.writeSucceeded)
            { Finish("Capture identity or write failed."); return; }
            if (_from == _to) { Evidence.pagesComplete = true; Finish(""); return; }
            if (_read == null)
            {
                _read = UPilotConsoleCaptureApi.ReadAsync(new ConsoleCaptureReadPayload
                {
                    sessionId = _session, fromSequence = _from, toSequence = _to - 1,
                    count = 1000, includeStackTrace = true, continuationToken = _continuation,
                }, _cancel.Token);
                return;
            }
            if (!_read.IsCompleted) return;
            ConsoleCaptureReadResult page;
            try { page = _read.GetAwaiter().GetResult(); }
            catch (Exception ex) { Evidence.readSucceeded = false; Finish(ex.Message); return; }
            finally { _read = null; }
            if (!page.ok || page.sessionId != _session)
            { Evidence.readSucceeded = false; Finish(page.error ?? "Capture page identity mismatch."); return; }
            // Do not accept a cursor pinned before the final boundary has reached disk.
            if (!_diskReady && page.diskSnapshotNextSequence < _to) return;
            _diskReady = true;
            foreach (var record in page.logs)
            {
                if (record.sequence != _next || record.sequence >= _to)
                { Evidence.pagesComplete = false; Finish("Capture sequence gap or duplicate."); return; }
                Records.Add(record); _next++;
            }
            if (page.truncated)
            {
                if (string.IsNullOrEmpty(page.continuationToken) || page.continuationToken == _continuation)
                { Finish("Capture cursor did not advance."); return; }
                _continuation = page.continuationToken;
            }
            else { Evidence.pagesComplete = _next == _to && page.scanComplete; Finish(""); }
        }
        private void Finish(string detail)
        { Evidence.detail = detail ?? ""; Complete = true; _cancel.Cancel(); }
        public void Dispose()
        {
            _cancel.Cancel();
            // Observe a worker fault without executing Unity API off the main thread.
            if (_read != null) _ = _read.ContinueWith(task => { var ignored = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            _cancel.Dispose();
        }
    }
}
