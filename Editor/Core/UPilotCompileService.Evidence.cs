// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable]
    public sealed class CompileMilestonePayload
    {
        public string stage, phase;
        public long revision, observedAt;
        public bool terminal, errorsVerified;
        public int errorCount;
    }

    [Serializable]
    public sealed class CompileEvidencePayload
    {
        public int version = 1;
        public string projectPath, producerEpoch, compileOperationId;
        public int processId;
        public long processCreatedAt, revision;
        public bool persistenceVerified;
        public List<CompileMilestonePayload> milestones = new();
    }

    public sealed partial class UPilotCompileService
    {
        private string _evidencePath;
        private string _evidenceProjectPath, _evidenceProducerEpoch;
        private int _evidenceProcessId;
        private long _evidenceProcessCreatedAt;
        private bool _evidenceIdentityConfigured;
        private CompileEvidencePayload _compileEvidence;
        private object _compileEvidenceLock;
        private object CompileEvidenceLock => System.Threading.LazyInitializer.EnsureInitialized(ref _compileEvidenceLock);
        private string EvidencePath => _evidencePath ?? CompileErrorsPath;

        // This observation belongs to the Editor process, not to a network connection.
        internal event Action<string> LocalStateChanged;

        // Isolated persistence seam: no live Unity subscriptions or compiler invocation.
        internal UPilotCompileService(string evidencePath, string projectPath, int processId,
            long processCreatedAt, string producerEpoch)
        {
            _evidencePath = evidencePath ?? throw new ArgumentNullException(nameof(evidencePath));
            ConfigureEvidenceIdentity(projectPath, processId, processCreatedAt, producerEpoch);
        }

        internal void ConfigureEvidenceIdentity(string projectPath, int processId,
            long processCreatedAt, string producerEpoch)
        {
            lock (CompileEvidenceLock)
            {
                if (string.IsNullOrWhiteSpace(projectPath) || processId <= 0 || processCreatedAt <= 0 ||
                    string.IsNullOrWhiteSpace(producerEpoch))
                    throw new ArgumentException("Compile evidence requires an exact project, process and producer identity.");
                _evidenceProjectPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                _evidenceProcessId = processId;
                _evidenceProcessCreatedAt = processCreatedAt;
                _evidenceProducerEpoch = producerEpoch;
                _evidenceIdentityConfigured = true;
                TryRestoreFromDisk();
            }
            MarkVerifyingAfterReload();
        }

        public CompileEvidencePayload BuildCompileEvidencePayload()
        {
            lock (CompileEvidenceLock) return CloneEvidence(_compileEvidence);
        }

        private static CompileEvidencePayload CloneEvidence(CompileEvidencePayload source) =>
            source == null ? null : JsonUtility.FromJson<CompileEvidencePayload>(JsonUtility.ToJson(source));

        private bool MatchesEvidenceIdentity(CompileEvidencePayload evidence) =>
            _evidenceIdentityConfigured && evidence != null && evidence.version == 1 &&
            evidence.processId == _evidenceProcessId && evidence.processCreatedAt == _evidenceProcessCreatedAt &&
            string.Equals(evidence.projectPath, _evidenceProjectPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(evidence.producerEpoch, _evidenceProducerEpoch, StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(_compileOperationId) && evidence.compileOperationId == _compileOperationId;

        private static bool IsMilestoneStage(string stage) => stage == "queued" || stage == "compiling" ||
            stage == "compiler_finished" || stage == "domain_reload" || stage == "verifying" || stage == "terminal";

        private static CompileMilestonePayload Milestone(CompileEvidencePayload evidence, string stage) =>
            evidence?.milestones?.Find(value => value != null && value.stage == stage);

        private static bool HasValidMilestones(CompileEvidencePayload evidence)
        {
            if (evidence == null || evidence.revision <= 0 || evidence.milestones == null ||
                evidence.milestones.Count == 0 || evidence.milestones.Count > 6) return false;
            var stages = new HashSet<string>(StringComparer.Ordinal);
            var revisions = new HashSet<long>();
            long latestRevision = 0;
            foreach (var item in evidence.milestones)
            {
                if (item == null || !IsMilestoneStage(item.stage) || !stages.Add(item.stage) ||
                    item.revision <= 0 || item.revision > evidence.revision || !revisions.Add(item.revision) ||
                    item.observedAt <= 0 || item.errorCount < 0) return false;
                if (item.stage == "terminal")
                {
                    if (!item.terminal || (item.phase != "completed" && item.phase != "failed")) return false;
                }
                else if (item.phase != item.stage || item.terminal || item.errorsVerified) return false;
                latestRevision = Math.Max(latestRevision, item.revision);
            }
            if (latestRevision != evidence.revision) return false;
            return true;
        }

        private bool RecordMilestoneAndPersist(string stage)
        {
            if (!_evidenceIdentityConfigured || string.IsNullOrEmpty(_compileOperationId))
            {
                Logger.ReportFailure("COMPILE", "Compile evidence identity is unavailable; phase=" + Phase,
                    scope: "compile-evidence|" + _compileOperationId, fingerprint: "identity_missing");
                return false;
            }
            if (!MatchesEvidenceIdentity(_compileEvidence))
                _compileEvidence = new CompileEvidencePayload
                {
                    projectPath = _evidenceProjectPath, processId = _evidenceProcessId,
                    processCreatedAt = _evidenceProcessCreatedAt, producerEpoch = _evidenceProducerEpoch,
                    compileOperationId = _compileOperationId,
                };
            var record = new CompileMilestonePayload
            {
                stage = stage, phase = Phase, revision = ++_compileEvidence.revision,
                observedAt = LastProgressAt, terminal = Terminal, errorsVerified = ErrorsVerified,
                errorCount = _lastErrors.Count,
            };
            var index = _compileEvidence.milestones.FindIndex(value => value.stage == stage);
            if (index < 0) _compileEvidence.milestones.Add(record);
            else _compileEvidence.milestones[index] = record;
            _compileEvidence.persistenceVerified = false;
            return PersistToDisk();
        }

        private void NotifyLocalState(string stage)
        {
            var phase = Phase;
            var errorsVerified = ErrorsVerified;
            var transition = stage == "queued" ? "compile_queued" : stage == "compiling" ? "compile_started" :
                stage == "domain_reload" ? "domain_reload_starting" : stage == "verifying" ? "domain_reload_recovered" :
                stage == "terminal" ? (errorsVerified && phase == "completed" ? "compile_completed" : "compile_failed") : stage;
            try { LocalStateChanged?.Invoke(transition); }
            catch (Exception ex)
            {
                Logger.ReportFailure("COMPILE", "Local compile state observation failed; phase=" + Phase, ex,
                    scope: "compile-evidence|" + _compileOperationId, fingerprint: "local_observer");
            }
        }

        private bool PersistToDisk(CompileErrorsPayload candidate = null)
        {
            string temporary = null;
            try
            {
                var payload = candidate ?? BuildLastCompileErrorsPayload(includeWarnings: true);
                if (!MatchesEvidenceIdentity(payload.compileEvidence) || !HasValidMilestones(payload.compileEvidence))
                    throw new InvalidDataException("Compile evidence identity or milestones are missing.");
                payload.compileEvidence.persistenceVerified = true;
                var json = JsonUtility.ToJson(payload, true);
                var directory = Path.GetDirectoryName(EvidencePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                temporary = EvidencePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, json);
                if (File.Exists(EvidencePath)) File.Replace(temporary, EvidencePath, null);
                else File.Move(temporary, EvidencePath);
                if (!string.Equals(File.ReadAllText(EvidencePath), json, StringComparison.Ordinal))
                    throw new InvalidDataException("Compile evidence readback differs from the saved record.");
                var readback = JsonUtility.FromJson<CompileErrorsPayload>(json);
                if (!MatchesEvidenceIdentity(readback?.compileEvidence) ||
                    readback.compileEvidence.revision != payload.compileEvidence.revision)
                    throw new InvalidDataException("Compile evidence readback identity or revision differs.");
                _compileEvidence.persistenceVerified = true;
                return true;
            }
            catch (Exception ex)
            {
                if (_compileEvidence != null) _compileEvidence.persistenceVerified = false;
                Logger.ReportFailure("COMPILE", "Persist/readback compile evidence failed; path=" + EvidencePath +
                    " compileOperationId=" + _compileOperationId + " phase=" + Phase, ex,
                    scope: "compile-evidence|" + _compileOperationId, fingerprint: "persist");
                return false;
            }
            finally
            {
                if (temporary != null && File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch (Exception ex)
                    {
                        Logger.ReportFailure("COMPILE", "Compile evidence temporary-file cleanup failed; path=" + temporary, ex,
                            scope: "compile-evidence|" + _compileOperationId, fingerprint: "temporary_cleanup");
                    }
                }
            }
        }

        private static bool ErrorsMatch(List<CompileErrorItemPayload> left, List<CompileErrorItemPayload> right)
        {
            if (left == null || right == null || left.Count != right.Count) return false;
            for (var index = 0; index < left.Count; index++)
            {
                var a = left[index]; var b = right[index];
                if (a == null || b == null || a.assemblyName != b.assemblyName || a.file != b.file ||
                    a.line != b.line || a.column != b.column || a.message != b.message || a.severity != b.severity) return false;
            }
            return true;
        }

        private bool HasMatchedCompilerResult(CompileErrorsPayload payload)
        {
            var evidence = payload?.compileEvidence;
            var started = Milestone(evidence, "compiling");
            var finished = Milestone(evidence, "compiler_finished");
            return MatchesEvidenceIdentity(evidence) && HasValidMilestones(evidence) && evidence.persistenceVerified &&
                evidence.compileOperationId == payload.compileOperationId &&
                started != null && finished != null && payload.startedAt > 0 && payload.lastCompileStartedAt == payload.startedAt &&
                started.observedAt == payload.startedAt && payload.lastCompilerFinishedAt >= payload.startedAt &&
                payload.lastCompilerFinishedAt > 0 && payload.finishedAt == payload.lastCompilerFinishedAt &&
                finished.observedAt == payload.lastCompilerFinishedAt && payload.errors != null &&
                payload.total == payload.errors.Count && finished.errorCount == payload.total;
        }

        private bool VerifyPersistedCompilerResult()
        {
            try
            {
                if (!File.Exists(EvidencePath)) throw new FileNotFoundException("Compiler result evidence is missing.", EvidencePath);
                var persisted = JsonUtility.FromJson<CompileErrorsPayload>(File.ReadAllText(EvidencePath));
                if (!HasMatchedCompilerResult(persisted) || _compileEvidence == null ||
                    !_compileEvidence.persistenceVerified ||
                    JsonUtility.ToJson(persisted.compileEvidence) != JsonUtility.ToJson(_compileEvidence) ||
                    persisted.phase != Phase || persisted.terminal != Terminal ||
                    persisted.errorsVerified != ErrorsVerified || persisted.verificationPending != VerificationPending ||
                    persisted.requestId != _lastRequestId || persisted.writeBatchId != _writeBatchId ||
                    persisted.writeBatchCreatedAt != _writeBatchCreatedAt || persisted.lastCompilerFinishedAt != LastCompilerFinishedAt ||
                    persisted.finishedAt != CompileFinishedAt || !ErrorsMatch(persisted.errors, _lastErrors))
                    throw new InvalidDataException("Compiler result evidence does not match the current compile identity, finish or errors.");
                return true;
            }
            catch (Exception ex)
            {
                ErrorsVerified = false;
                Logger.ReportFailure("COMPILE", "Compile verification refused; compileOperationId=" + _compileOperationId, ex,
                    scope: "compile-evidence|" + _compileOperationId, fingerprint: "verification");
                return false;
            }
        }

        private void ApplyRestoredEvidence(CompileErrorsPayload payload)
        {
            _compileEvidence = CloneEvidence(payload.compileEvidence);
            var matched = MatchesEvidenceIdentity(_compileEvidence) && HasValidMilestones(_compileEvidence) &&
                _compileEvidence.persistenceVerified;
            var latest = matched ? _compileEvidence.milestones.Find(value => value.revision == _compileEvidence.revision) : null;
            matched = matched && latest != null && latest.phase == payload.phase &&
                latest.terminal == payload.terminal && latest.errorsVerified == payload.errorsVerified &&
                latest.errorCount == payload.total && payload.total == _lastErrors.Count &&
                latest.observedAt == payload.lastProgressAt;
            var terminal = Milestone(_compileEvidence, "terminal");
            ErrorsVerified = matched && payload.errorsVerified && HasMatchedCompilerResult(payload) &&
                terminal != null && terminal.errorsVerified && terminal.terminal &&
                terminal.phase == payload.phase && terminal.errorCount == payload.total &&
                terminal.revision == _compileEvidence.revision &&
                ((payload.total == 0 && payload.phase == "completed") || (payload.total > 0 && payload.phase == "failed")) &&
                terminal.observedAt == payload.lastCompileVerifiedAt && payload.lastCompileVerifiedAt >= payload.lastCompilerFinishedAt;
            Terminal = matched && payload.terminal && (!payload.errorsVerified || ErrorsVerified);
            if (!matched || (payload.errorsVerified && !ErrorsVerified) ||
                (payload.phase == "completed" && !ErrorsVerified))
            {
                // Keep diagnostic details, but historical/legacy data cannot certify this Editor process.
                Phase = "unknown"; Status = "unknown"; IsCompiling = false; IsRequestCompileActive = false;
                Terminal = false; ErrorsVerified = false;
                VerificationPending = false;
                _compileEvidence = null;
            }
        }
    }
}
