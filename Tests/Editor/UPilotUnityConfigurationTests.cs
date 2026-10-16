using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotUnityConfigurationTests
    {
        private bool attempted;
        private int preference, preferenceWrites, defineWrites;
        private string defines;
        private readonly List<string> order = new List<string>();
        private readonly List<string> logs = new List<string>();
        private readonly List<string> exceptions = new List<string>();
        private bool oldIgnore, oldConsole;
        private Logger.LogLevel oldLevel;

        [SetUp] public void Setup()
        {
            attempted = false; preference = 0; preferenceWrites = defineWrites = 0; defines = "GAME";
            order.Clear(); logs.Clear(); exceptions.Clear();
            oldIgnore = LogAssert.ignoreFailingMessages;
            oldConsole = Logger.LogToUnityConsole; oldLevel = Logger.MinLevel;
            Logger.LogToUnityConsole = false; Logger.MinLevel = Logger.LogLevel.Error;
            Application.logMessageReceived += Capture;
        }

        [TearDown] public void Teardown()
        {
            Application.logMessageReceived -= Capture;
            Logger.LogToUnityConsole = oldConsole; Logger.MinLevel = oldLevel;
            LogAssert.ignoreFailingMessages = oldIgnore;
        }

        private void Capture(string message, string stack, LogType type)
        {
            if (message.Contains("[UPilot][Unity配置]")) logs.Add(message);
            if (type == LogType.Exception) exceptions.Add(message + stack);
        }

        private UPilotUnityConfiguration New(Action mark = null, Func<int> read = null, Action<int> write = null)
        {
            return new UPilotUnityConfiguration(() => attempted, mark ?? (() => attempted = true),
                read ?? (() => preference), write ?? (value => { Assert.That(attempted, Is.True); order.Add("policy"); preference = value; preferenceWrites++; }),
                () => defines, () => { order.Add("define"); defines += ";UPILOT"; defineWrites++; });
        }

        [Test] public void ContinuousThreeSecondsResetsOnEveryInterruptionAndRunsOnceAcrossReload()
        {
            var config = New();
            config.Tick(0, false); config.Tick(100, false); // Initially playing/disconnected.
            config.Tick(101, true); config.Tick(103.999, true);
            Assert.That(attempted, Is.False);
            config.Tick(104, false); config.Tick(200, true); config.Tick(202.999, true);
            Assert.That(attempted, Is.False);
            config.Tick(203, true);
            Assert.That(attempted, Is.True); Assert.That(order, Is.EqualTo(new[] { "policy", "define" }));
            int count = logs.Count;
            config.ResetDelay(); config.Tick(300, false); config.Tick(400, true); config.Tick(403, true);
            var reloaded = New(); reloaded.Tick(500, true); reloaded.Tick(503, true);
            Assert.That(logs.Count, Is.EqualTo(count));
            Assert.That(preferenceWrites, Is.EqualTo(1)); Assert.That(defineWrites, Is.EqualTo(1));
        }

        [Test] public void CorrectSettingsNeverWriteAndAlwaysLogInventoryItemsAndSummary()
        {
            preference = 1; defines = "GAME;UPILOT";
            New().CheckAndApplyStartupConfiguration();
            Assert.That(preferenceWrites + defineWrites, Is.Zero);
            Assert.That(logs.Count, Is.EqualTo(6));
            Assert.That(logs[0], Does.Contain("2项").And.Contain("播放时脚本编译策略").And.Contain("UPILOT 编译宏"));
            Assert.That(logs.Count(value => value.Contains("原值已符合")), Is.EqualTo(2));
            Assert.That(logs.Last(), Does.Contain("成功2，失败0，未执行0").And.Contain("全部成功"));
        }

        [Test] public void ChangedSettingsAreReadBackAndOrdered()
        {
            int reads = 0;
            New(read: () => { reads++; return preference; }).CheckAndApplyStartupConfiguration();
            Assert.That(reads, Is.EqualTo(2));
            Assert.That(order, Is.EqualTo(new[] { "policy", "define" }));
            Assert.That(logs.Count(value => value.Contains("已修改并验证")), Is.EqualTo(2));
        }

        [TestCase("读取")][TestCase("写入")][TestCase("回读")]
        public void ItemFailurePreservesExceptionContinuesAndNeverReplays(string phase)
        {
            LogAssert.ignoreFailingMessages = true;
            int reads = 0;
            var config = New(read: () => { reads++; if (phase == "读取" || phase == "回读" && reads == 2) throw new InvalidOperationException("read evidence"); return preference; },
                write: value => { if (phase == "写入") throw new InvalidOperationException("write evidence"); preference = value; });
            config.CheckAndApplyStartupConfiguration();
            Assert.That(defineWrites, Is.EqualTo(1));
            Assert.That(logs.Any(value => value.Contains("阶段=" + phase)), Is.True);
            Assert.That(logs.Last(), Does.Contain("成功1，失败1，未执行0").And.Contain("总体=未全部成功"));
            Assert.That(exceptions.Single(), Does.Contain("evidence").And.Contain("UPilotUnityConfigurationTests"));
            int count = logs.Count; New().CheckAndApplyStartupConfiguration();
            Assert.That(logs.Count, Is.EqualTo(count));
        }

        [Test] public void ReadbackMismatchFailsWithoutRetrying()
        {
            LogAssert.ignoreFailingMessages = true;
            New(write: value => preferenceWrites++).CheckAndApplyStartupConfiguration();
            Assert.That(preferenceWrites, Is.EqualTo(1));
            Assert.That(logs.Any(value => value.Contains("阶段=回读") && value.Contains("实际=0")), Is.True);
            Assert.That(logs.Last(), Does.Contain("成功1，失败1，未执行0"));
        }

        [Test] public void MarkerFailureReportsUnexecutedAndDoesNotRetry()
        {
            LogAssert.ignoreFailingMessages = true;
            var config = New(mark: () => throw new InvalidOperationException("isolated marker failed"));
            config.CheckAndApplyStartupConfiguration(); config.CheckAndApplyStartupConfiguration();
            Assert.That(preferenceWrites + defineWrites, Is.Zero);
            Assert.That(logs.Last(), Does.Contain("成功0，失败0，未执行2").And.Contain("未全部成功"));
        }
    }
}
