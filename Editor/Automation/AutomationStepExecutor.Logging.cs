using System;
using System.Collections.Generic;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    public sealed partial class AutomationStepExecutor
    {
        private AutomationStepExecutionLog _textLog;
        private readonly HashSet<string> _loggedStepFinishes = new();
        private bool _textLogFinished;

        private void Trace(string eventType, string label, Func<string> fields = null, long at = 0)
        {
            if (_run == null || string.IsNullOrEmpty(_run.executionLogTimeZoneId)) return;
            try { (_textLog ??= new AutomationStepExecutionLog()).Write(_run, at > 0 ? at : _clock(), eventType, label, fields?.Invoke()); }
            catch (Exception ex) { ReportExecutionLogFailure(ex); }
        }

        private void ReportExecutionLogFailure(Exception error)
        {
            try { (_textLog ??= new AutomationStepExecutionLog()).Report(_run, "format", error); }
            catch { /* Even the diagnostic destination may be unavailable. */ }
        }

        private void StartExecutionLog()
        {
            _textLogFinished = false;
            _loggedStepFinishes.Clear();
            try
            {
                _run.executionLogTimeZoneId = TimeZoneInfo.Local.Id;
                Trace("run.started", "任务开始", () => "步骤数=" + _run.plan.steps.Length, _run.startedAtUtcMs);
                Trace("run.plan", "任务计划", () => "计划=" + JsonUtility.ToJson(_run.plan));
            }
            catch (Exception ex) { ReportExecutionLogFailure(ex); }
        }

        private void RestoreExecutionLog()
        {
            _textLogFinished = _run.terminal && !ReleasingImmediateStopResources;
            _loggedStepFinishes.Clear();
            foreach (var step in _run.steps) if (step.stage == "Completed") _loggedStepFinishes.Add(step.instanceId);
            if (!_textLogFinished) Trace("run.restoring", "恢复原任务观察", () => "阶段=" + _run.stage);
        }

        private string StepFields(int index)
        {
            var item = _run.plan.steps[index];
            return "序号=" + (index + 1) + " instanceId=" + AutomationStepExecutionLog.Quote(item.instanceId)
                + " stepId=" + AutomationStepExecutionLog.Quote(item.stepId) + " 阶段=" + item.phase;
        }

        private void TraceStep(string eventType, string label, Func<string> fields = null, long at = 0)
        {
            int index = _run.cursor;
            Trace(eventType, label, () => StepFields(index) + (fields == null ? "" : " " + fields()), at);
        }

        private string ErrorFields(AutomationStepError error) => "错误码=" + AutomationStepExecutionLog.Quote(error?.code)
            + " 详情=" + AutomationStepExecutionLog.Quote(error?.message)
            + " 诊断=" + AutomationStepExecutionLog.Quote(error?.diagnostic);

        private void TraceExecutionFinished(long at)
        {
            TraceStep("step.execution_finished", "步骤执行结束", () => "状态=" + Current.outcome
                + " 开始=" + AutomationStepExecutionLog.Timestamp(_run, Current.startedAtUtcMs)
                + " 结束=" + AutomationStepExecutionLog.Timestamp(_run, at)
                + " 执行耗时=" + AutomationStepExecutionLog.Elapsed(Current.startedAtUtcMs, at)
                + " " + ErrorFields(Current.error), at);
        }

        private void TraceCleanupFinished(string state)
        {
            if (Current.executionLogCleanupFinishedAtUtcMs > 0) return;
            try
            {
                long at = _clock();
                Current.executionLogCleanupFinishedAtUtcMs = at;
                TraceStep("step.cleanup_finished", "步骤清理结束", () => "状态=" + state
                    + " 开始=" + AutomationStepExecutionLog.Timestamp(_run, Current.cleanupStartedAtUtcMs)
                    + " 结束=" + AutomationStepExecutionLog.Timestamp(_run, at)
                    + " 清理耗时=" + AutomationStepExecutionLog.Elapsed(Current.cleanupStartedAtUtcMs, at)
                    + " " + ErrorFields(Current.error), at);
            }
            catch (Exception ex) { ReportExecutionLogFailure(ex); }
        }

        private void TraceStepFinished(int index, long at, string outcome = null)
        {
            var record = _run.steps[index];
            if (!_loggedStepFinishes.Add(record.instanceId)) return;
            Trace("step.finished", "步骤结束", () =>
            {
                long executionEnd = record.cleanupStartedAtUtcMs > 0 ? record.cleanupStartedAtUtcMs : at;
                return StepFields(index) + " 状态=" + (outcome ?? record.outcome.ToString())
                    + " 开始=" + AutomationStepExecutionLog.Timestamp(_run, record.startedAtUtcMs)
                    + " 结束=" + (record.startedAtUtcMs > 0 ? AutomationStepExecutionLog.Timestamp(_run, at) : "未知")
                    + " 执行结束=" + AutomationStepExecutionLog.Timestamp(_run, record.startedAtUtcMs > 0 ? executionEnd : 0)
                    + " 清理开始=" + AutomationStepExecutionLog.Timestamp(_run, record.cleanupStartedAtUtcMs)
                    + " 清理结束=" + AutomationStepExecutionLog.Timestamp(_run, record.executionLogCleanupFinishedAtUtcMs)
                    + " 执行耗时=" + AutomationStepExecutionLog.Elapsed(record.startedAtUtcMs, executionEnd)
                    + " 清理耗时=" + AutomationStepExecutionLog.Elapsed(record.cleanupStartedAtUtcMs, record.executionLogCleanupFinishedAtUtcMs)
                    + " 总耗时=" + AutomationStepExecutionLog.Elapsed(record.startedAtUtcMs, at)
                    + " 清理状态=" + record.cleanupState + " " + ErrorFields(record.error);
            }, at);
        }

        internal void TraceInterruption(string eventType, string label)
        {
            if (_run != null && !_textLogFinished && (!_run.terminal || ReleasingImmediateStopResources)) Trace(eventType, label);
        }

        internal void FinishExecutionLog(string releaseDetail = "")
        {
            if (_run == null || _textLogFinished || string.IsNullOrEmpty(_run.executionLogTimeZoneId)) return;
            Logger.CloseDiagnostics(_run.runId);
            _textLogFinished = true;
            try
            {
                long now = _clock();
                for (int i = 0; i < _run.steps.Count; i++)
                {
                    var step = _run.steps[i];
                    if (!_loggedStepFinishes.Contains(step.instanceId))
                        TraceStepFinished(i, step.stage == "Completed" && step.finishedAtUtcMs > 0 ? step.finishedAtUtcMs : now,
                            step.startedAtUtcMs <= 0 ? "未执行" : step.stage == "Completed" ? null : "已中断");
                }
                Trace("run.finished", "任务结束", () => "状态=" + _run.status
                    + " 开始=" + AutomationStepExecutionLog.Timestamp(_run, _run.startedAtUtcMs)
                    + " 结束=" + AutomationStepExecutionLog.Timestamp(_run, now)
                    + " 总耗时=" + AutomationStepExecutionLog.Elapsed(_run.startedAtUtcMs, now)
                    + " " + ErrorFields(_run.error)
                    + " 次要错误=" + JsonUtility.ToJson(new Errors { items = _run.secondaryErrors.ToArray() })
                    + " 资源收尾=" + AutomationStepExecutionLog.Quote(releaseDetail)
                    + " 资源异常=" + AutomationStepExecutionLog.Quote(_run.resourceRecoveryError), now);
            }
            catch (Exception ex) { ReportExecutionLogFailure(ex); }
        }

        [Serializable] private sealed class Errors { public AutomationStepError[] items; }
    }
}
