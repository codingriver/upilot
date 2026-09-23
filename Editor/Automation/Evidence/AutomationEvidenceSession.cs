using System;
using System.Collections.Generic;

namespace CodingRiver.UPilot.Automation
{
    /// <summary>Owned or borrowed Capture identity. Borrowed sessions can never be stopped here.</summary>
    public sealed class AutomationEvidenceSession
    {
        private readonly string _ownerToken;
        public string SessionId { get; }
        public bool OwnsCapture => _ownerToken != null;
        private AutomationEvidenceSession(string sessionId, string ownerToken)
        { SessionId = sessionId; _ownerToken = ownerToken; }
        public static AutomationEvidenceSession Borrow(string sessionId)
        {
            var result = UPilotConsoleCaptureApi.Status(sessionId);
            if (!result.ok || result.session?.sessionId != sessionId || !result.session.active)
                throw new InvalidOperationException("STEP_CAPTURE_IDENTITY_INVALID");
            return new AutomationEvidenceSession(sessionId, null);
        }
        public static AutomationEvidenceSession StartOwned(ConsoleCaptureStartPayload request)
        {
            if (request == null || string.IsNullOrEmpty(request.ownerToken))
                throw new ArgumentException("An explicit owner token is required.");
            var result = UPilotConsoleCaptureApi.Start(request);
            if (!result.ok || result.session == null) throw new InvalidOperationException(result.error);
            return new AutomationEvidenceSession(result.session.sessionId, request.ownerToken);
        }
        public long Boundary()
        {
            var result = UPilotConsoleCaptureApi.GetBoundary(SessionId);
            if (!result.ok) throw new InvalidOperationException(result.errorCode + ": " + result.errorMessage);
            return result.nextSequence;
        }
        public ConsoleCaptureResult StopOwned()
        {
            if (!OwnsCapture) throw new InvalidOperationException("STEP_CAPTURE_NOT_OWNED");
            return UPilotConsoleCaptureApi.Stop(SessionId, _ownerToken);
        }
        public static void AddInterval(List<AutomationLogInterval> intervals, string phase, string instanceId, long from, long to)
        {
            if (from < 0 || to < from) throw new ArgumentException("Invalid interval.");
            if (to == from) return;
            intervals.Add(new AutomationLogInterval
            { phaseId = phase, caseId = instanceId, fromSequenceInclusive = from, toSequenceExclusive = to });
        }
    }
}
