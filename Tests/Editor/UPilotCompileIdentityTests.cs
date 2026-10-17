using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotCompileIdentityTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private string _directory, _path;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "UPilotCompileIdentityTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _path = Path.Combine(_directory, "CompileErrors.json");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CompilationStartDoesNotInheritPriorCompletionEvidence(bool requestActive)
        {
            var service = CreateIsolatedService();
            SetField(service, "_lastErrors", new List<CompileErrorItemPayload>
            {
                new CompileErrorItemPayload { message = "old error" }
            });
            SetField(service, "_lastWarnings", new List<CompileErrorItemPayload>
            {
                new CompileErrorItemPayload { message = "old warning" }
            });
            SetField(service, "_lastWarningCount", 4);
            SetField(service, "_warningDetailsAvailable", false);
            SetField(service, "_warningsTruncated", true);
            SetField(service, "_lastRequestId", "request-before");
            SetField(service, "_compileOperationId", "operation-before");
            SetField(service, "_writeBatchId", "batch-before");
            SetField(service, "_writeBatchCreatedAt", 100L);
            SetField(service, "_reloadId", "reload-before");
            SetProperty(service, "IsRequestCompileActive", requestActive);
            SetProperty(service, "LastCompileRequestedAt", 100L);
            foreach (var name in new[] { "CompileStartedAt", "CompileFinishedAt", "LastCompileStartedAt",
                "LastCompilerFinishedAt", "LastCompileVerifiedAt", "LastTerminalCompileAt" })
                SetProperty(service, name, 200L);
            SetProperty(service, "Terminal", true);
            SetProperty(service, "ErrorsVerified", true);
            SetProperty(service, "HasCompileErrors", true);

            typeof(UPilotCompileService).GetMethod("OnCompilationStarted", PrivateInstance)
                .Invoke(service, new object[] { null });

            Assert.That(service.Phase, Is.EqualTo("compiling"));
            Assert.That(service.Terminal, Is.False);
            Assert.That(service.ErrorsVerified, Is.False);
            Assert.That(service.VerificationPending, Is.True);
            Assert.That(service.LastCompileStartedAt, Is.GreaterThan(200));
            Assert.That(service.CompileFinishedAt, Is.Zero);
            Assert.That(service.LastCompilerFinishedAt, Is.Zero);
            Assert.That(service.LastCompileVerifiedAt, Is.Zero);
            Assert.That(service.LastTerminalCompileAt, Is.Zero);
            Assert.That(service.LastErrorCount, Is.Zero);
            Assert.That(service.LastWarningCount, Is.Zero);
            Assert.That(service.HasCompileErrors, Is.False);
            var diagnostics = service.BuildLastCompileErrorsPayload(includeWarnings: true);
            Assert.That(diagnostics.warningDetailsAvailable, Is.True);
            Assert.That(diagnostics.warningsTruncated, Is.False);
            Assert.That(diagnostics.warnings, Is.Empty);
            if (requestActive)
            {
                Assert.That(service.CompileOperationId, Is.EqualTo("operation-before"));
                Assert.That(service.LastRequestId, Is.EqualTo("request-before"));
                Assert.That(service.WriteBatchId, Is.EqualTo("batch-before"));
                Assert.That(service.WriteBatchCreatedAt, Is.EqualTo(100));
            }
            else
            {
                Assert.That(service.CompileOperationId, Is.Not.Empty.And.Not.EqualTo("operation-before"));
                Assert.That(service.LastRequestId, Is.Empty);
                Assert.That(service.WriteBatchId, Is.Empty);
                Assert.That(service.WriteBatchCreatedAt, Is.Zero);
                Assert.That(service.LastCompileRequestedAt, Is.Zero);
                Assert.That(service.ReloadId, Is.Empty);
                Assert.That(service.CompileOrigin, Is.EqualTo("unity_auto"));
            }
        }

        [Test]
        public void LegacyPersistedWarningCountWithoutDetailsRemainsUnknown()
        {
            const string legacyPayload = "{\"requestId\":\"legacy-request\",\"status\":\"finished\",\"phase\":\"completed\",\"terminal\":true,\"errorsVerified\":true,\"warningCount\":4,\"errors\":[]}";
            WithPersistedCompilePayload(legacyPayload, () =>
            {
                var service = CreateIsolatedService();
                service.TryRestoreFromDisk();

                var diagnostics = service.BuildLastCompileErrorsPayload(includeWarnings: true);
                Assert.That(diagnostics.warningCount, Is.EqualTo(4));
                Assert.That(diagnostics.warningDetailsAvailable, Is.False);
                Assert.That(diagnostics.warningsTruncated, Is.False);
                Assert.That(diagnostics.warnings, Is.Null);
            });
        }

        [Test]
        public void RestoredWarningsAreBoundedToOneThousandAndRemainTruncated()
        {
            var persisted = new CompileErrorsPayload
            {
                requestId = "warning-boundary",
                warningCount = 1001,
                warningDetailsAvailable = true,
                warnings = new List<CompileErrorItemPayload>(),
            };
            for (var index = 0; index < 1001; index++)
                persisted.warnings.Add(new CompileErrorItemPayload { message = "warning-" + index });

            WithPersistedCompilePayload(UnityEngine.JsonUtility.ToJson(persisted), () =>
            {
                var service = CreateIsolatedService();
                service.TryRestoreFromDisk();

                var diagnostics = service.BuildLastCompileErrorsPayload(includeWarnings: true);
                Assert.That(diagnostics.warningDetailsAvailable, Is.True);
                Assert.That(diagnostics.warningCount, Is.EqualTo(1001));
                Assert.That(diagnostics.warnings, Has.Count.EqualTo(1000));
                Assert.That(diagnostics.warnings[0].message, Is.EqualTo("warning-0"));
                Assert.That(diagnostics.warnings[999].message, Is.EqualTo("warning-999"));
                Assert.That(diagnostics.warningsTruncated, Is.True);
            });
        }

        private UPilotCompileService CreateIsolatedService() =>
            new UPilotCompileService(_path, _directory, 123, 456, "isolated-producer");

        private void WithPersistedCompilePayload(string json, Action assertion)
        {
            File.WriteAllText(_path, json);
            assertion();
        }

        private static void SetField(object instance, string name, object value) =>
            typeof(UPilotCompileService).GetField(name, PrivateInstance).SetValue(instance, value);

        private static void SetProperty(object instance, string name, object value) =>
            typeof(UPilotCompileService).GetProperty(name).GetSetMethod(true).Invoke(instance, new[] { value });
    }
}
