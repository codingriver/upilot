// User-wide download settings use isolated files; never touch real user preferences.
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotUserSettingsTests
    {
        private string _directory;
        private string ConfigPath => Path.Combine(_directory, ".upilot", "settings.json");

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "UPilotUserSettingsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        [Test]
        public void MissingFileUsesDefaultsWithoutCreatingConfiguration()
        {
            var settings = UPilotUserSettings.Load(ConfigPath, out var error);
            AssertDefaults(settings);
            Assert.That(error, Is.Empty);
            Assert.That(File.Exists(ConfigPath), Is.False);
        }

        [TestCase("{}", 524288L, 8)]
        [TestCase("{\"maxConcurrentSegmentRequests\":3}", 524288L, 3)]
        [TestCase("{\"maxSegmentSizeBytes\":1024}", 1024L, 8)]
        [TestCase(" {\"unknown\":{\"text\":\"} \\\" {\"},\"maxConcurrentSegmentRequests\":3} \r\n", 524288L, 3)]
        public void MissingFieldsRetainDefaults(string json, long size, int concurrent)
        {
            WriteConfig(json);
            var settings = UPilotUserSettings.Load(ConfigPath, out var error);
            Assert.That(error, Is.Empty);
            Assert.That(settings.maxSegmentSizeBytes, Is.EqualTo(size));
            Assert.That(settings.maxConcurrentSegmentRequests, Is.EqualTo(concurrent));
            Assert.That(File.ReadAllText(ConfigPath), Is.EqualTo(json));
        }

        [TestCase("")]
        [TestCase("{broken")]
        [TestCase("[]")]
        [TestCase("null")]
        [TestCase("{} {}")]
        [TestCase("{} []")]
        [TestCase("{} false")]
        [TestCase("{\"maxSegmentSizeBytes\":0,\"maxConcurrentSegmentRequests\":3}")]
        [TestCase("{\"maxSegmentSizeBytes\":1025,\"maxConcurrentSegmentRequests\":3}")]
        [TestCase("{\"maxSegmentSizeBytes\":1024,\"maxConcurrentSegmentRequests\":0}")]
        [TestCase("{\"maxSegmentSizeBytes\":1024,\"maxConcurrentSegmentRequests\":-1}")]
        [TestCase("{\"maxSegmentSizeBytes\":1024.5}")]
        [TestCase("{\"maxSegmentSizeBytes\":\"1024\"}")]
        [TestCase("{\"maxSegmentSizeBytes\":null}")]
        [TestCase("{\"maxSegmentSizeBytes\":9223372036854775808}")]
        [TestCase("{\"maxConcurrentSegmentRequests\":2147483648}")]
        [TestCase("{\"maxConcurrentSegmentRequests\":2,\"maxConcurrentSegmentRequests\":3}")]
        public void InvalidFileFallsBackEntirelyAndPreservesOriginal(string json)
        {
            WriteConfig(json);
            ExpectFailure("load", ConfigPath);
            var settings = UPilotUserSettings.Load(ConfigPath, out var error);
            AssertDefaults(settings);
            Assert.That(error, Does.Contain(ConfigPath));
            Assert.That(File.ReadAllText(ConfigPath), Is.EqualTo(json));
        }

        [Test]
        public void ReadFailureIsVisibleAndUsesDefaults()
        {
            Directory.CreateDirectory(ConfigPath);
            ExpectFailure("load", ConfigPath);
            AssertDefaults(UPilotUserSettings.Load(ConfigPath, out var error));
            Assert.That(error, Does.Contain(ConfigPath));
        }

        [Test]
        public void SavingCreatesConfigurationWithOnlyTheTwoUserFields()
        {
            var settings = new UPilotUserSettingsData
            {
                maxSegmentSizeBytes = 256 * 1024,
                maxConcurrentSegmentRequests = 12,
            };
            Assert.That(UPilotUserSettings.TrySave(ConfigPath, settings, out var error), Is.True, error);
            var json = File.ReadAllText(ConfigPath);
            Assert.That(Regex.Matches(json, "\"[^\"]+\"\\s*:").Count, Is.EqualTo(2));
            var loaded = UPilotUserSettings.Load(ConfigPath, out error);
            Assert.That(error, Is.Empty);
            Assert.That(loaded.maxSegmentSizeBytes, Is.EqualTo(256 * 1024));
            Assert.That(loaded.maxConcurrentSegmentRequests, Is.EqualTo(12));
        }

        [Test]
        public void FailedWriteKeepsBlockingFileAndReportsFailure()
        {
            var blocker = Path.Combine(_directory, "blocked");
            File.WriteAllText(blocker, "preserve this file");
            var path = Path.Combine(blocker, "settings.json");
            ExpectFailure("save", path);
            Assert.That(UPilotUserSettings.TrySave(path, new UPilotUserSettingsData(), out var error), Is.False);
            Assert.That(error, Does.Contain(path));
            Assert.That(File.ReadAllText(blocker), Is.EqualTo("preserve this file"));
        }

        [TestCase(0L, 8)]
        [TestCase(1025L, 8)]
        [TestCase(1024L, 0)]
        [TestCase(1024L, -1)]
        public void InvalidSaveLeavesLastSavedConfigurationUnchanged(long size, int concurrent)
        {
            Assert.That(UPilotUserSettings.TrySave(ConfigPath, new UPilotUserSettingsData(), out _), Is.True);
            var original = File.ReadAllText(ConfigPath);
            ExpectFailure("save", ConfigPath);
            Assert.That(UPilotUserSettings.TrySave(ConfigPath, new UPilotUserSettingsData
            {
                maxSegmentSizeBytes = size,
                maxConcurrentSegmentRequests = concurrent,
            }, out var error), Is.False);
            Assert.That(error, Is.Not.Empty);
            Assert.That(File.ReadAllText(ConfigPath), Is.EqualTo(original));
        }

        [Test]
        public void SavingDefaultsRestoresBothUserSettings()
        {
            Assert.That(UPilotUserSettings.TrySave(ConfigPath, new UPilotUserSettingsData
            {
                maxSegmentSizeBytes = 1024,
                maxConcurrentSegmentRequests = 2,
            }, out _), Is.True);
            Assert.That(UPilotUserSettings.TrySave(ConfigPath, new UPilotUserSettingsData(), out var error), Is.True, error);
            AssertDefaults(UPilotUserSettings.Load(ConfigPath, out error));
            Assert.That(error, Is.Empty);
        }

        [Test]
        public void IndependentConsumersReloadSharedSettingsAndDownloadOptionsStayFrozen()
        {
            Assert.That(UPilotUserSettings.TrySave(ConfigPath, new UPilotUserSettingsData(), out _), Is.True);
            var first = UPilotUserSettings.Load(ConfigPath, out _);
            var firstOptions = UPilotUserSettings.CreateDownloadOptions(first);
            first.maxSegmentSizeBytes = 1024;
            first.maxConcurrentSegmentRequests = 2;
            Assert.That(UPilotUserSettings.TrySave(ConfigPath, first, out _), Is.True);
            var next = UPilotUserSettings.CreateDownloadOptions(UPilotUserSettings.Load(ConfigPath, out var error));
            Assert.That(error, Is.Empty);
            Assert.That(next.MaxSegmentSizeBytes, Is.EqualTo(1024));
            Assert.That(next.MaxConcurrentSegmentRequests, Is.EqualTo(2));
            Assert.That(firstOptions.MaxSegmentSizeBytes, Is.EqualTo(524288));
            Assert.That(firstOptions.MaxConcurrentSegmentRequests, Is.EqualTo(8));
        }

        private void WriteConfig(string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            File.WriteAllText(ConfigPath, json);
        }

        private static void ExpectFailure(string operation, string path) =>
            LogAssert.Expect(LogType.Error, new Regex(Regex.Escape($"[UPilotUserSettings] operation={operation}; path={path}; error=")));

        private static void AssertDefaults(UPilotUserSettingsData settings)
        {
            Assert.That(settings.maxSegmentSizeBytes, Is.EqualTo(524288));
            Assert.That(settings.maxConcurrentSegmentRequests, Is.EqualTo(8));
        }
    }
}
