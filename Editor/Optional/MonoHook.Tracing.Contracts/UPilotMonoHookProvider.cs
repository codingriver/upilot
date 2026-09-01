// -----------------------------------------------------------------------
// UPilot Editor - public MonoHook point extension contracts.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace CodingRiver.UPilot
{
    /// <summary>
    /// Controls whether a tracing point is allowed to change the target method's
    /// behavior. PassThrough is the safe default: the original method must still
    /// be called with its original arguments and result semantics.
    /// </summary>
    public enum UPilotMonoHookExecutionMode
    {
        PassThrough = 0,
        Intercept = 1,
    }

    /// <summary>
    /// Optional provider capability used by the controller's pass-through safety
    /// gate. Providers that install method replacements should explicitly declare
    /// what they guarantee instead of making the controller infer behavior.
    /// </summary>
    public interface IUPilotMonoHookExecutionPolicyProvider
    {
        bool GuaranteesPassThrough { get; }
        bool SupportsInterception { get; }
        UPilotMonoHookExecutionMode ExecutionMode { get; set; }
    }

    public enum UPilotMonoHookOperationStatus
    {
        Succeeded,
        Unchanged,
        Unsupported,
        Failed,
    }

    public sealed class UPilotMonoHookSupport
    {
        public bool IsSupported { get; }
        public string Message { get; }

        private UPilotMonoHookSupport(bool isSupported, string message)
        {
            IsSupported = isSupported;
            Message = message ?? string.Empty;
        }

        public static UPilotMonoHookSupport Supported(string message = "") =>
            new UPilotMonoHookSupport(true, message);

        public static UPilotMonoHookSupport Unsupported(string message) =>
            new UPilotMonoHookSupport(false, message);
    }

    /// <summary>
    /// Read-only installation detail captured while a point is applied. The
    /// detail is a target-type/method snapshot; it never represents an instance
    /// allocation or an event-time lookup.
    /// </summary>
    [Serializable]
    public sealed class UPilotMonoHookInstallEntry
    {
        public string TargetTypeName { get; }
        public string DeclaringTypeName { get; }
        public string MethodSignature { get; }
        public string TargetMethodId { get; }
        public string Status { get; }
        public string Reason { get; }
        public string TrampolineKey { get; }

        public UPilotMonoHookInstallEntry(
            string targetTypeName,
            string declaringTypeName,
            string methodSignature,
            string targetMethodId,
            string status,
            string reason = "",
            string trampolineKey = "")
        {
            TargetTypeName = targetTypeName ?? string.Empty;
            DeclaringTypeName = declaringTypeName ?? string.Empty;
            MethodSignature = methodSignature ?? string.Empty;
            TargetMethodId = targetMethodId ?? string.Empty;
            Status = status ?? string.Empty;
            Reason = reason ?? string.Empty;
            TrampolineKey = trampolineKey ?? string.Empty;
        }
    }

    public sealed class UPilotMonoHookCoverage
    {
        private readonly List<string> _samples;
        private readonly List<UPilotMonoHookInstallEntry> _entries;

        public int CandidateCount { get; }
        public int InstalledCount { get; }
        public int InstalledTypeCount { get; }
        public int InstalledMethodCount { get; }
        public int TrampolineCount { get; }
        public int SkippedCount { get; }
        public int FailedCount { get; }
        public IReadOnlyList<string> Samples => _samples;
        public IReadOnlyList<UPilotMonoHookInstallEntry> Entries => _entries;
        public bool IsPartial => InstalledCount > 0 && (SkippedCount > 0 || FailedCount > 0);

        public UPilotMonoHookCoverage(
            int candidateCount,
            int installedCount,
            int skippedCount,
            int failedCount,
            IEnumerable<string> samples = null,
            IEnumerable<UPilotMonoHookInstallEntry> entries = null)
        {
            CandidateCount = Math.Max(0, candidateCount);
            InstalledCount = Math.Max(0, installedCount);
            SkippedCount = Math.Max(0, skippedCount);
            FailedCount = Math.Max(0, failedCount);
            _samples = samples == null ? new List<string>() : new List<string>(samples);
            _entries = entries == null
                ? new List<UPilotMonoHookInstallEntry>()
                : new List<UPilotMonoHookInstallEntry>(entries);

            var installedEntries = _entries
                .Where(entry => entry != null && string.Equals(entry.Status, "Installed", StringComparison.Ordinal))
                .ToList();
            InstalledTypeCount = installedEntries
                .Select(entry => entry.TargetTypeName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct(StringComparer.Ordinal)
                .Count();
            InstalledMethodCount = installedEntries
                .Select(entry => entry.TargetMethodId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.Ordinal)
                .Count();
            TrampolineCount = installedEntries
                .Select(entry => entry.TrampolineKey)
                .Where(key => !string.IsNullOrEmpty(key))
                .Distinct(StringComparer.Ordinal)
                .Count();

            if (InstalledTypeCount == 0 && InstalledCount > 0)
                InstalledTypeCount = InstalledCount;
            if (InstalledMethodCount == 0 && InstalledCount > 0)
                InstalledMethodCount = InstalledCount;
            if (TrampolineCount == 0 && InstalledCount > 0)
                TrampolineCount = InstalledCount;
        }

        public string BuildSummary()
        {
            if (CandidateCount <= 0) return "未发现候选方法";
            if (IsPartial) return $"部分覆盖：已安装 {InstalledCount}/{CandidateCount}，跳过 {SkippedCount}，失败 {FailedCount}";
            if (InstalledCount > 0) return $"已安装 {InstalledCount}/{CandidateCount}";
            return $"未安装：候选 {CandidateCount}，跳过 {SkippedCount}，失败 {FailedCount}";
        }
    }

    public sealed class UPilotMonoHookInstallResult
    {
        public UPilotMonoHookOperationStatus Status { get; }
        public string Message { get; }
        public bool Success => Status == UPilotMonoHookOperationStatus.Succeeded ||
                               Status == UPilotMonoHookOperationStatus.Unchanged;

        private UPilotMonoHookInstallResult(UPilotMonoHookOperationStatus status, string message)
        {
            Status = status;
            Message = message ?? string.Empty;
        }

        public static UPilotMonoHookInstallResult Succeeded(string message = "") =>
            new UPilotMonoHookInstallResult(UPilotMonoHookOperationStatus.Succeeded, message);

        public static UPilotMonoHookInstallResult Unchanged(string message = "") =>
            new UPilotMonoHookInstallResult(UPilotMonoHookOperationStatus.Unchanged, message);

        public static UPilotMonoHookInstallResult Unsupported(string message) =>
            new UPilotMonoHookInstallResult(UPilotMonoHookOperationStatus.Unsupported, message);

        public static UPilotMonoHookInstallResult Failed(string message) =>
            new UPilotMonoHookInstallResult(UPilotMonoHookOperationStatus.Failed, message);
    }

    public interface IUPilotMonoHookPointProvider
    {
        bool IsInstalled { get; }
        UPilotMonoHookSupport CheckSupport(UPilotMonoHookContext context);
        UPilotMonoHookInstallResult Install(UPilotMonoHookContext context);
        UPilotMonoHookInstallResult Uninstall(UPilotMonoHookContext context);
    }

    public interface IUPilotMonoHookCoverageProvider
    {
        UPilotMonoHookCoverage Coverage { get; }
    }

    /// <summary>
    /// Optional capability for points that can switch between a minimal recommended
    /// overload set and every overload that passes the current safety checks.
    /// </summary>
    public interface IUPilotMonoHookOverloadPolicyProvider
    {
        bool SupportsHookAllSafeOverloads { get; }
        bool HookAllSafeOverloads { get; set; }
        bool IsHookAllSafeOverloadsApplied { get; }
    }

    public interface IUPilotMonoHookEventSink
    {
        long Publish(UPilotMonoHookEvent hookEvent);
    }

    public interface IUPilotMonoHookHandle : IDisposable
    {
        bool IsInstalled { get; }
        void Uninstall();
    }

    public interface IUPilotMonoHookFactory
    {
        IUPilotMonoHookHandle Install(MethodBase target, MethodInfo replacement, MethodInfo proxy, string ownerId);
    }

    public sealed class UPilotMonoHookContext
    {
        public IUPilotMonoHookFactory HookFactory { get; }
        public IUPilotMonoHookEventSink EventSink { get; }
        public string UnityVersion { get; }

        internal UPilotMonoHookContext(
            IUPilotMonoHookFactory hookFactory,
            IUPilotMonoHookEventSink eventSink,
            string unityVersion)
        {
            HookFactory = hookFactory ?? throw new ArgumentNullException(nameof(hookFactory));
            EventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
            UnityVersion = unityVersion ?? string.Empty;
        }
    }

    public sealed class UPilotMonoHookBinding
    {
        public MethodBase Target { get; }
        public MethodInfo Replacement { get; }
        public MethodInfo Proxy { get; }
        public string Tag { get; }

        public UPilotMonoHookBinding(MethodBase target, MethodInfo replacement, MethodInfo proxy, string tag = null)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            Replacement = replacement ?? throw new ArgumentNullException(nameof(replacement));
            Proxy = proxy;
            Tag = tag ?? string.Empty;
        }
    }

    public abstract class UPilotMonoHookPointBase : IUPilotMonoHookPointProvider
    {
        private bool _isInstalled;

        public virtual bool IsInstalled => _isInstalled;

        public virtual UPilotMonoHookSupport CheckSupport(UPilotMonoHookContext context) =>
            UPilotMonoHookSupport.Supported();

        public UPilotMonoHookInstallResult Install(UPilotMonoHookContext context)
        {
            if (IsInstalled) return UPilotMonoHookInstallResult.Unchanged("已安装");
            var support = CheckSupport(context);
            if (support == null || !support.IsSupported)
                return UPilotMonoHookInstallResult.Unsupported(support?.Message ?? "该点位不受支持");

            try
            {
                InstallCore(context);
                _isInstalled = true;
                return UPilotMonoHookInstallResult.Succeeded("已安装");
            }
            catch (Exception ex)
            {
                try { UninstallCore(context); } catch { }
                _isInstalled = false;
                return UPilotMonoHookInstallResult.Failed(ex.Message);
            }
        }

        public UPilotMonoHookInstallResult Uninstall(UPilotMonoHookContext context)
        {
            if (!IsInstalled) return UPilotMonoHookInstallResult.Unchanged("未安装");
            try
            {
                UninstallCore(context);
                _isInstalled = false;
                return UPilotMonoHookInstallResult.Succeeded("已卸载");
            }
            catch (Exception ex)
            {
                return UPilotMonoHookInstallResult.Failed(ex.Message);
            }
        }

        protected abstract void InstallCore(UPilotMonoHookContext context);
        protected abstract void UninstallCore(UPilotMonoHookContext context);
    }

    public abstract class UPilotMethodHookPointBase : UPilotMonoHookPointBase
    {
        private readonly List<IUPilotMonoHookHandle> _handles = new List<IUPilotMonoHookHandle>();

        public override bool IsInstalled => _handles.Count > 0 && _handles.TrueForAll(handle => handle.IsInstalled);

        protected abstract IEnumerable<UPilotMonoHookBinding> CreateBindings(UPilotMonoHookContext context);

        protected override void InstallCore(UPilotMonoHookContext context)
        {
            foreach (var binding in CreateBindings(context))
            {
                if (binding == null) continue;
                _handles.Add(context.HookFactory.Install(binding.Target, binding.Replacement, binding.Proxy, binding.Tag));
            }
            if (_handles.Count == 0)
                throw new InvalidOperationException("点位未返回任何可安装的 MonoHook 绑定。");
        }

        protected override void UninstallCore(UPilotMonoHookContext context)
        {
            for (int i = _handles.Count - 1; i >= 0; i--)
                _handles[i]?.Uninstall();
            _handles.Clear();
        }
    }
}
