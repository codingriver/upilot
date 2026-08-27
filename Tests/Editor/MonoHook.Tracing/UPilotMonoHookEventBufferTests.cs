// -----------------------------------------------------------------------
// UPilot Editor tests - MonoHook event buffer.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotMonoHookEventBufferTests
    {
        [Test]
        public void BufferIsBoundedAndSequencesAreMonotonic()
        {
            var buffer = new UPilotMonoHookEventBuffer(2);

            var first = buffer.Add(new UPilotMonoHookEvent { kind = "first" });
            var second = buffer.Add(new UPilotMonoHookEvent { kind = "second" });
            var third = buffer.Add(new UPilotMonoHookEvent { kind = "third" });

            Assert.That(first, Is.LessThan(second));
            Assert.That(second, Is.LessThan(third));
            Assert.That(buffer.Count, Is.EqualTo(2));
            Assert.That(buffer.DroppedCount, Is.EqualTo(1));
            Assert.That(buffer.Read(10)[0].kind, Is.EqualTo("second"));
        }

        [Test]
        public void ReadRemovesOnlyRequestedNumberOfEvents()
        {
            var buffer = new UPilotMonoHookEventBuffer(4);
            buffer.Add(new UPilotMonoHookEvent { kind = "one" });
            buffer.Add(new UPilotMonoHookEvent { kind = "two" });
            buffer.Add(new UPilotMonoHookEvent { kind = "three" });

            Assert.That(buffer.Read(2).Count, Is.EqualTo(2));
            Assert.That(buffer.Count, Is.EqualTo(1));
            Assert.That(buffer.Read(1)[0].kind, Is.EqualTo("three"));
        }

        [Test]
        public void SnapshotReturnsLatestEventsWithoutRemovingThem()
        {
            var buffer = new UPilotMonoHookEventBuffer(4);
            buffer.Add(new UPilotMonoHookEvent { kind = "one" });
            buffer.Add(new UPilotMonoHookEvent { kind = "two" });
            buffer.Add(new UPilotMonoHookEvent { kind = "three" });

            var snapshot = buffer.Snapshot(2);

            Assert.That(snapshot.Count, Is.EqualTo(2));
            Assert.That(snapshot[0].kind, Is.EqualTo("two"));
            Assert.That(snapshot[1].kind, Is.EqualTo("three"));
            Assert.That(buffer.Count, Is.EqualTo(3));
        }

        [Test]
        public void TelemetrySuppressesUnchangedValuesAndAppliesRateLimit()
        {
            var settings = UPilotMonoHookSettings.instance;
            bool oldSuppress = settings.suppressUnchangedValues;
            int oldLimit = settings.maxEventsPerSecond;
            try
            {
                settings.suppressUnchangedValues = true;
                settings.maxEventsPerSecond = 1;
                UPilotMonoHookTelemetry.Clear();

                var sink = UPilotMonoHookRegistry.Instance.Context.EventSink;
                Assert.That(sink.Publish(new UPilotMonoHookEvent
                {
                    kind = "unchanged",
                    beforeValue = "same",
                    afterValue = "same",
                }), Is.Zero);
                Assert.That(sink.Publish(new UPilotMonoHookEvent { kind = "first" }), Is.GreaterThan(0));
                Assert.That(sink.Publish(new UPilotMonoHookEvent { kind = "second" }), Is.Zero);

                Assert.That(UPilotMonoHookTelemetry.Count, Is.EqualTo(1));
                Assert.That(UPilotMonoHookTelemetry.DroppedCount, Is.EqualTo(1));
            }
            finally
            {
                settings.suppressUnchangedValues = oldSuppress;
                settings.maxEventsPerSecond = oldLimit;
                UPilotMonoHookTelemetry.Clear();
            }
        }

        [Test]
        public void TelemetryCanExportJsonLinesWithoutConsumingBuffer()
        {
            string path = Path.Combine(Path.GetTempPath(), "UPilotMonoHook_" + Guid.NewGuid().ToString("N") + ".jsonl");
            try
            {
                UPilotMonoHookTelemetry.Clear();
                UPilotMonoHookRegistry.Instance.Context.EventSink.Publish(
                    new UPilotMonoHookEvent { kind = "export.test" });

                int count = UPilotMonoHookTelemetry.ExportJsonLines(path);

                Assert.That(count, Is.EqualTo(1));
                Assert.That(File.ReadAllText(path), Does.Contain("export.test"));
                Assert.That(UPilotMonoHookTelemetry.Count, Is.EqualTo(1));
            }
            finally
            {
                UPilotMonoHookTelemetry.Clear();
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void TelemetryCapturesSampledStackTraceOnlyForSelectedPoint()
        {
            var settings = UPilotMonoHookSettings.instance;
            int oldFrames = settings.stackTraceMaxFrames;
            int oldSample = settings.stackTraceSampleEveryN;
            bool oldCapture = settings.ShouldCaptureStackTrace(UPilotMonoHookPointId.GameObjectSetActive);
            try
            {
                settings.stackTraceMaxFrames = 4;
                settings.stackTraceSampleEveryN = 2;
                settings.SetCaptureStackTrace(UPilotMonoHookPointId.GameObjectSetActive, true);
                UPilotMonoHookTelemetry.Clear();

                var sink = UPilotMonoHookRegistry.Instance.Context.EventSink;
                sink.Publish(new UPilotMonoHookEvent { pointId = UPilotMonoHookPointId.GameObjectSetActive, kind = "first" });
                sink.Publish(new UPilotMonoHookEvent { pointId = UPilotMonoHookPointId.GameObjectSetActive, kind = "second" });
                sink.Publish(new UPilotMonoHookEvent { pointId = UPilotMonoHookPointId.TransformPosition, kind = "other" });

                var events = UPilotMonoHookTelemetry.Snapshot(3);
                Assert.That(events[0].stackTrace, Is.Not.Empty);
                Assert.That(events[1].stackTrace, Is.Null.Or.Empty);
                Assert.That(events[2].stackTrace, Is.Null.Or.Empty);
                Assert.That(events[0].stackTrace.Split('\n').Length, Is.LessThanOrEqualTo(4));
            }
            finally
            {
                settings.stackTraceMaxFrames = oldFrames;
                settings.stackTraceSampleEveryN = oldSample;
                settings.SetCaptureStackTrace(UPilotMonoHookPointId.GameObjectSetActive, oldCapture);
                UPilotMonoHookTelemetry.Clear();
            }
        }

        [Test]
        public void TelemetryWritesFormattedConsoleLogsWithIndependentRateLimit()
        {
            var settings = UPilotMonoHookSettings.instance;
            bool oldConsoleEnabled = settings.logEventsToConsole;
            int oldConsoleLimit = settings.maxConsoleLogsPerSecond;
            bool oldSuppress = settings.suppressUnchangedValues;
            int oldEventLimit = settings.maxEventsPerSecond;
            try
            {
                settings.logEventsToConsole = true;
                settings.maxConsoleLogsPerSecond = 1;
                settings.suppressUnchangedValues = false;
                settings.maxEventsPerSecond = 1000;
                UPilotMonoHookTelemetry.Clear();

                LogAssert.Expect(
                    LogType.Log,
                    new Regex("^\\[UPilot\\]\\[Trace\\] #\\d+ F42 point=\\\"component\\.rendererEnabled\\\" phase=\\\"after\\\" scene=\\\"Assets/Scenes/Test\\.unity\\\" object=\\\"Root/Renderer\\\" component=\\\"UnityEngine\\.MeshRenderer\\\" method=\\\"set_enabled\\(Boolean\\)\\\" value=\\\"True -> False\\\"$"));

                var sink = UPilotMonoHookRegistry.Instance.Context.EventSink;
                sink.Publish(new UPilotMonoHookEvent
                {
                    pointId = UPilotMonoHookPointId.ComponentRendererEnabled,
                    kind = UPilotMonoHookPointId.ComponentRendererEnabled,
                    phase = "after",
                    frame = 42,
                    objectName = "Renderer",
                    hierarchyPath = "Root/Renderer",
                    scenePath = "Assets/Scenes/Test.unity",
                    componentType = typeof(MeshRenderer).FullName,
                    methodSignature = "set_enabled(Boolean)",
                    beforeValue = "True",
                    afterValue = "False",
                });
                sink.Publish(new UPilotMonoHookEvent { pointId = "tests.console.second", kind = "tests.console.second" });

                Assert.That(UPilotMonoHookTelemetry.Count, Is.EqualTo(2));
                Assert.That(UPilotMonoHookTelemetry.ConsoleDroppedCount, Is.EqualTo(1));
            }
            finally
            {
                settings.logEventsToConsole = oldConsoleEnabled;
                settings.maxConsoleLogsPerSecond = oldConsoleLimit;
                settings.suppressUnchangedValues = oldSuppress;
                settings.maxEventsPerSecond = oldEventLimit;
                UPilotMonoHookTelemetry.Clear();
            }
        }

        [Test]
        public void ConsoleFormatterAppendsCapturedHookStack()
        {
            string formatted = UPilotMonoHookInstallationService.FormatConsoleLog(new UPilotMonoHookEvent
            {
                sequence = 7,
                frame = 12,
                pointId = "tests.console.stack",
                stackTrace = "Game.TestCaller()",
            });

            Assert.That(formatted, Does.StartWith("[UPilot][Trace] #7 F12 point=\"tests.console.stack\""));
            Assert.That(formatted, Does.EndWith("Hook caller:\nGame.TestCaller()"));
        }
    }
}
