using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace CodingRiver.UPilot
{
    /// <summary>One attempt per Editor session, after authenticated, stable EditMode.</summary>
    internal sealed class UPilotUnityConfiguration
    {
        internal const string CompilationPreference = "ScriptCompilationDuringPlay";
        private const string AttemptedKey = "CodingRiver.UPilot.UnityConfiguration.Attempted";
        private const string Prefix = "[UPilot][Unity配置] ";
        private static readonly UPilotUnityConfiguration Startup = new UPilotUnityConfiguration(
            () => SessionState.GetBool(AttemptedKey, false), () => SessionState.SetBool(AttemptedKey, true),
            () => EditorPrefs.GetInt(CompilationPreference, -1), value => EditorPrefs.SetInt(CompilationPreference, value),
            ReadDefines, UPilotScriptingDefineManager.EnsureForActiveBuildTarget);
        private readonly Func<bool> attempted;
        private readonly Action markAttempted;
        private readonly Func<int> readPreference;
        private readonly Action<int> writePreference;
        private readonly Func<string> readDefines;
        private readonly Action ensureDefine;
        private double? stableSince;
        private bool attemptedLocally;
        private object connection;

        internal UPilotUnityConfiguration(Func<bool> attempted, Action markAttempted,
            Func<int> readPreference, Action<int> writePreference, Func<string> readDefines, Action ensureDefine)
        {
            this.attempted = attempted; this.markAttempted = markAttempted;
            this.readPreference = readPreference; this.writePreference = writePreference;
            this.readDefines = readDefines; this.ensureDefine = ensureDefine;
        }

        internal static void Update(bool connected, object connectionIdentity)
        {
            if (!ReferenceEquals(Startup.connection, connectionIdentity))
            {
                Startup.ResetDelay();
                Startup.connection = connectionIdentity;
            }
            Startup.Tick(EditorApplication.timeSinceStartup, connected &&
                UPilotPlayModeCompilePolicy.CurrentMode == "edit" &&
                !EditorApplication.isCompiling && !EditorApplication.isUpdating);
        }

        internal static void ResetStartupDelay() => Startup.ResetDelay();
        internal void ResetDelay() => stableSince = null;

        internal void Tick(double now, bool eligible)
        {
            if (attemptedLocally || attempted()) return;
            if (!eligible) { ResetDelay(); return; }
            if (!stableSince.HasValue) stableSince = now;
            if (now - stableSince.Value < 3) return;
            CheckAndApplyStartupConfiguration();
        }

        internal void CheckAndApplyStartupConfiguration()
        {
            if (attemptedLocally || attempted()) return;
            attemptedLocally = true;
            var names = new[] { "播放时脚本编译策略", "UPILOT 编译宏" };
            var targets = new[] { "RecompileAfterFinishedPlaying (ScriptCompilationDuringPlay=1)", "当前构建目标包含 UPILOT" };
            var results = new[] { "未执行", "未执行" };
            Log("开始：2项；" + string.Join("、", names) + "；目标=" + string.Join("、", targets) + "。");
            try
            {
                // Persist BEFORE any setting write; reload/reconnect must never replay an attempt.
                markAttempted();
                ApplyItem(0, names, targets, results, () => readPreference().ToString(), value => value == "1", () => writePreference(1));
                ApplyItem(1, names, targets, results, readDefines,
                    value => (value ?? "").Split(';').Any(symbol => symbol.Trim() == "UPILOT"), ensureDefine);
            }
            catch (Exception ex)
            {
                Error("入口中断；未执行项原因=" + ex.Message);
                Debug.LogException(ex);
            }
            finally
            {
                int success = results.Count(value => value == "成功");
                int failed = results.Count(value => value == "失败");
                int skipped = results.Length - success - failed;
                var summary = $"完成：成功{success}，失败{failed}，未执行{skipped}；" +
                    string.Join("，", names.Select((name, index) => name + "=" + results[index])) +
                    (success == results.Length ? "；总体=全部成功。" : "；总体=未全部成功。");
                if (failed + skipped > 0) Error(summary); else Log(summary);
            }
        }

        private static void ApplyItem(int index, string[] names, string[] targets, string[] results,
            Func<string> read, Func<string, bool> matches, Action write)
        {
            string phase = "读取", actual = "未知";
            bool changed = false, writeAttempted = false;
            Log("开始项：" + names[index] + "；目标=" + targets[index]);
            try
            {
                actual = read();
                if (!matches(actual))
                {
                    phase = "写入";
                    writeAttempted = true;
                    write(); changed = true;
                    phase = "回读";
                    actual = "未知（回读未完成）";
                    actual = read();
                    if (!matches(actual)) throw new InvalidOperationException("回读与目标不一致");
                }
                results[index] = "成功";
                Log("成功：" + names[index] + (changed ? "；已修改并验证，回读一致" : "；原值已符合，无需修改") + "；实际=" + actual);
            }
            catch (Exception ex)
            {
                results[index] = "失败";
                Error($"失败：{names[index]}；阶段={phase}；写入已尝试={writeAttempted}；写入已返回={changed}；期望={targets[index]}；实际={actual}；原因={ex.Message}");
                Debug.LogException(ex);
            }
        }

        private static string ReadDefines()
        {
            var group = EditorUserBuildSettings.selectedBuildTargetGroup;
            if (group == BuildTargetGroup.Unknown) throw new InvalidOperationException("当前构建目标组未知");
            return PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.FromBuildTargetGroup(group));
        }

        private static void Log(string message) => Debug.Log(Prefix + message);
        private static void Error(string message) => Debug.LogError(Prefix + message);
    }
}
