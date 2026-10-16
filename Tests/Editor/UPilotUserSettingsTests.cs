// User-wide settings use isolated files; never touch real user preferences.
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using ProcessWindowStyle = System.Diagnostics.ProcessWindowStyle;
using Stopwatch = System.Diagnostics.Stopwatch;
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


        [TestCase(null)]
        [TestCase("{}")]
        [TestCase("{\"maxSegmentSizeBytes\":1048576,\"custom\":true}")]
        [TestCase("{\"projectPorts\":{}}")]
        public void MissingPortRegistrationIsEmptyWithoutCreatingOrRewritingFile(string json)
        {
            if (json != null) WriteConfig(json);
            Assert.That(UPilotUserSettings.ReadProjectPorts(ConfigPath), Is.Empty);
            Assert.That(File.Exists(ConfigPath), Is.EqualTo(json != null));
            if (json != null) Assert.That(File.ReadAllText(ConfigPath), Is.EqualTo(json));
        }

        [Test]
        public void PortRegistrationRoundTripsProjectsAndUsesNormalizedAbsolutePaths()
        {
            var first = Path.Combine(_directory, "Projects", "First Project");
            var second = Path.Combine(_directory, "Projects", "Second");
            var firstAlias = Path.Combine(first, ".", "..", "First Project") + Path.DirectorySeparatorChar;
            UPilotUserSettings.SetProjectPorts(ConfigPath, firstAlias, 8765, 8011);
            UPilotUserSettings.SetProjectPorts(ConfigPath, second, 8766, 8012);
            var ports = UPilotUserSettings.ReadProjectPorts(ConfigPath);
            Assert.That(ports.Count, Is.EqualTo(2));
            Assert.That(ports[first.Replace('\\', '/')], Is.EqualTo(new[] { 8765, 8011 }));
            Assert.That(ports[second.Replace('\\', '/')], Is.EqualTo(new[] { 8766, 8012 }));
            Assert.That(ports.Keys, Has.None.Contains("\\"));
            Assert.That(ports.Keys, Has.None.EndsWith("/"));
            Assert.That(ports.Keys, Has.None.Contains("/./"));
        }

        [Test]
        public void UpdatingOneProjectKeepsOtherProjectRegistration()
        {
            var first = ProjectPath("First");
            var second = ProjectPath("Second");
            UPilotUserSettings.SetProjectPorts(ConfigPath, first, 8765, 8011);
            UPilotUserSettings.SetProjectPorts(ConfigPath, second, 8766, 8012);
            UPilotUserSettings.SetProjectPorts(ConfigPath, first + "/.", 9000, 9001);
            var ports = UPilotUserSettings.ReadProjectPorts(ConfigPath);
            Assert.That(ports.Count, Is.EqualTo(2));
            Assert.That(ports[first], Is.EqualTo(new[] { 9000, 9001 }));
            Assert.That(ports[second], Is.EqualTo(new[] { 8766, 8012 }));
        }

        [Test]
        public void WindowsProjectRegistrationMatchesCaseInsensitively()
        {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows path identity check.");
            var project = ProjectPath("MixedCase");
            UPilotUserSettings.SetProjectPorts(ConfigPath, project, 8765, 8011);
            UPilotUserSettings.SetProjectPorts(ConfigPath, project.ToUpperInvariant() + "/", 9000, 9001);
            var ports = UPilotUserSettings.ReadProjectPorts(ConfigPath);
            Assert.That(ports.Count, Is.EqualTo(1));
            Assert.That(ports[project.ToLowerInvariant()], Is.EqualTo(new[] { 9000, 9001 }));
        }

        [TestCase("null")]
        [TestCase("[]")]
        [TestCase("{}")]
        [TestCase("[8765]")]
        [TestCase("[8765,8011,9000]")]
        [TestCase("[\"8765\",8011]")]
        [TestCase("[8765.5,8011]")]
        [TestCase("[8765,null]")]
        [TestCase("[true,8011]")]
        [TestCase("[0,8011]")]
        [TestCase("[-1,8011]")]
        [TestCase("[65536,8011]")]
        [TestCase("[8765,65536]")]
        [TestCase("[8765,8765]")]
        public void InvalidRegisteredPortArrayIsRejectedWithoutRewritingFile(string array)
        {
            var json = "{\"projectPorts\":{" + JsonString(ProjectPath("Invalid")) + ":" + array + "}}";
            WriteConfig(json);
            Assert.That(() => UPilotUserSettings.ReadProjectPorts(ConfigPath), Throws.Exception);
            Assert.That(File.ReadAllText(ConfigPath), Is.EqualTo(json));
        }

        [TestCase("null")]
        [TestCase("[]")]
        [TestCase("\"invalid\"")]
        public void PortRegistryMustBeAnObject(string value)
        {
            var json = "{\"projectPorts\":" + value + "}";
            WriteConfig(json);
            Assert.That(() => UPilotUserSettings.ReadProjectPorts(ConfigPath), Throws.Exception);
            Assert.That(File.ReadAllText(ConfigPath), Is.EqualTo(json));
        }

        [TestCase("")]
        [TestCase("relative/project")]
        public void RegisteredProjectPathMustBeAbsolute(string project)
        {
            var json = "{\"projectPorts\":{" + JsonString(project) + ":[8765,8011]}}";
            WriteConfig(json);
            Assert.That(() => UPilotUserSettings.ReadProjectPorts(ConfigPath), Throws.Exception);
            Assert.That(() => UPilotUserSettings.SetProjectPorts(ConfigPath, project, 8765, 8011), Throws.Exception);
            Assert.That(File.ReadAllText(ConfigPath), Is.EqualTo(json));
        }

        [Test]
        public void DuplicateNormalizedProjectKeysAreRejected()
        {
            var project = ProjectPath("Duplicate");
            var json = "{\"projectPorts\":{" + JsonString(project) + ":[8765,8011]," +
                JsonString(project + "/.") + ":[8766,8012]}}";
            WriteConfig(json);
            Assert.That(() => UPilotUserSettings.ReadProjectPorts(ConfigPath), Throws.Exception);
            Assert.That(File.ReadAllText(ConfigPath), Is.EqualTo(json));
        }

        [TestCase("C:relative")]
        [TestCase("/relative")]
        [TestCase("\\relative")]
        public void WindowsDriveRelativeProjectPathsAreRejected(string project)
        {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows path identity check.");
            var original = "{\"projectPorts\":{" + JsonString(project) + ":[8765,8011]}}";
            WriteConfig(original);
            Assert.That(() => UPilotUserSettings.NormalizeProjectPath(project), Throws.Exception);
            Assert.That(() => UPilotUserSettings.ReadProjectPorts(ConfigPath), Throws.Exception);
            Assert.That(() => UPilotUserSettings.SetProjectPorts(ConfigPath, project, 8765, 8011), Throws.Exception);
            Assert.That(File.ReadAllText(ConfigPath), Is.EqualTo(original));
        }

        [Test]
        public void SamePortUpdateCanonicalizesAnExistingNoncanonicalProjectKey()
        {
            var project = ProjectPath("Canonical");
            WriteConfig("{\"projectPorts\":{" + JsonString(project + "/.") + ":[8765,8011]}}");
            UPilotUserSettings.SetProjectPorts(ConfigPath, project, 8765, 8011);
            var rawKeys = ReadJsonField(File.ReadAllText(ConfigPath), "projectPorts").Elements()
                .Select(field => (string)field.Attribute("item") ?? field.Name.LocalName).ToArray();
            Assert.That(rawKeys, Is.EqualTo(new[] { project }));
            Assert.That(UPilotUserSettings.ReadProjectPorts(ConfigPath)[project], Is.EqualTo(new[] { 8765, 8011 }));
        }

        [Test]
        public void WindowsCaseEquivalentProjectKeysAreRejected()
        {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows path identity check.");
            var project = ProjectPath("MixedCase");
            var json = "{\"projectPorts\":{" + JsonString(project) + ":[8765,8011]," +
                JsonString(project.ToUpperInvariant()) + ":[8766,8012]}}";
            WriteConfig(json);
            Assert.That(() => UPilotUserSettings.ReadProjectPorts(ConfigPath), Throws.Exception);
        }

        [Test]
        public void DuplicatePortRegistryFieldsAreRejected()
        {
            WriteConfig("{\"projectPorts\":{},\"projectPorts\":{}}");
            Assert.That(() => UPilotUserSettings.ReadProjectPorts(ConfigPath), Throws.Exception);
        }

        [TestCase(0, 8011)]
        [TestCase(8765, 65536)]
        [TestCase(8765, 8765)]
        public void InvalidPortUpdatePreservesOriginalDocument(int ws, int http)
        {
            const string original = "{\"custom\":\"keep\",\"projectPorts\":{}}";
            WriteConfig(original);
            Assert.That(() => UPilotUserSettings.SetProjectPorts(ConfigPath, ProjectPath("Invalid"), ws, http), Throws.Exception);
            Assert.That(File.ReadAllText(ConfigPath), Is.EqualTo(original));
        }

        [Test]
        public void InvalidPortSectionDoesNotAffectValidDownloadSettingsOrTheirSave()
        {
            const string original = "{\"maxSegmentSizeBytes\":1048576,\"maxConcurrentSegmentRequests\":3,\"projectPorts\":\"invalid\",\"custom\":{\"text\":\"keep\"}}";
            WriteConfig(original);
            var loaded = UPilotUserSettings.Load(ConfigPath, out var error);
            Assert.That(error, Is.Empty);
            Assert.That(loaded.maxSegmentSizeBytes, Is.EqualTo(1048576));
            Assert.That(loaded.maxConcurrentSegmentRequests, Is.EqualTo(3));
            Assert.That(UPilotUserSettings.TrySave(ConfigPath, new UPilotUserSettingsData(), out error), Is.True, error);
            AssertFieldPreserved(original, "projectPorts");
            AssertFieldPreserved(original, "custom");
            Assert.That(() => UPilotUserSettings.ReadProjectPorts(ConfigPath), Throws.Exception);
        }

        [Test]
        public void InvalidDownloadSectionDoesNotAffectPortReadsUpdatesOrClear()
        {
            var project = ProjectPath("Independent");
            var original = "{\"maxSegmentSizeBytes\":\"invalid\",\"maxConcurrentSegmentRequests\":0,\"custom\":{\"text\":\"keep\"},\"projectPorts\":{" + JsonString(project) + ":[8765,8011]}}";
            WriteConfig(original);
            Assert.That(UPilotUserSettings.ReadProjectPorts(ConfigPath)[project], Is.EqualTo(new[] { 8765, 8011 }));
            UPilotUserSettings.SetProjectPorts(ConfigPath, project, 9000, 9001);
            Assert.That(UPilotUserSettings.ReadProjectPorts(ConfigPath)[project], Is.EqualTo(new[] { 9000, 9001 }));
            AssertFieldPreserved(original, "maxSegmentSizeBytes");
            AssertFieldPreserved(original, "maxConcurrentSegmentRequests");
            AssertFieldPreserved(original, "custom");
            UPilotUserSettings.ClearProjectPorts(ConfigPath);
            Assert.That(UPilotUserSettings.ReadProjectPorts(ConfigPath), Is.Empty);
            AssertFieldPreserved(original, "maxSegmentSizeBytes");
            AssertFieldPreserved(original, "maxConcurrentSegmentRequests");
            AssertFieldPreserved(original, "custom");
        }

        [Test]
        public void SavingAndRestoringDownloadDefaultsPreservesAllProjectsAndUnknownFields()
        {
            var first = ProjectPath("First");
            var second = ProjectPath("Second");
            var original = "{\"custom\":{\"text\":\"keep\",\"list\":[1,true,null,{\"nested\":\"value\"}]},\"projectPorts\":{" +
                JsonString(first) + ":[8765,8011]," + JsonString(second) + ":[8766,8012]}}";
            WriteConfig(original);
            Assert.That(UPilotUserSettings.TrySave(ConfigPath, new UPilotUserSettingsData
            {
                maxSegmentSizeBytes = 1048576,
                maxConcurrentSegmentRequests = 3,
            }, out var error), Is.True, error);
            AssertFieldPreserved(original, "projectPorts");
            AssertFieldPreserved(original, "custom");
            Assert.That(UPilotUserSettings.TrySave(ConfigPath, new UPilotUserSettingsData(), out error), Is.True, error);
            AssertDefaults(UPilotUserSettings.Load(ConfigPath, out error));
            Assert.That(error, Is.Empty);
            AssertFieldPreserved(original, "projectPorts");
            AssertFieldPreserved(original, "custom");
        }

        [Test]
        public void UpdatingAndClearingPortRegistryPreservesDownloadsAndUnknownFields()
        {
            const string original = "{\"maxSegmentSizeBytes\":1048576,\"maxConcurrentSegmentRequests\":3,\"custom\":{\"text\":\"keep\",\"list\":[1,true,null]}}";
            WriteConfig(original);
            UPilotUserSettings.SetProjectPorts(ConfigPath, ProjectPath("Registered"), 8765, 8011);
            AssertFieldPreserved(original, "maxSegmentSizeBytes");
            AssertFieldPreserved(original, "maxConcurrentSegmentRequests");
            AssertFieldPreserved(original, "custom");
            UPilotUserSettings.ClearProjectPorts(ConfigPath);
            Assert.That(UPilotUserSettings.ReadProjectPorts(ConfigPath), Is.Empty);
            Assert.That((string)ReadJsonField(File.ReadAllText(ConfigPath), "projectPorts").Attribute("type"), Is.EqualTo("object"));
            AssertFieldPreserved(original, "maxSegmentSizeBytes");
            AssertFieldPreserved(original, "maxConcurrentSegmentRequests");
            AssertFieldPreserved(original, "custom");
        }

        [TestCase("")]
        [TestCase("{broken")]
        [TestCase("[]")]
        [TestCase("{} {}")]
        public void MalformedDocumentRejectsEveryUpdateAndPreservesOriginal(string original)
        {
            WriteConfig(original);
            ExpectFailure("save", ConfigPath);
            Assert.That(UPilotUserSettings.TrySave(ConfigPath, new UPilotUserSettingsData(), out var error), Is.False);
            Assert.That(error, Is.Not.Empty);
            Assert.That(File.ReadAllText(ConfigPath), Is.EqualTo(original));
            Assert.That(() => UPilotUserSettings.SetProjectPorts(ConfigPath, ProjectPath("Rejected"), 8765, 8011), Throws.Exception);
            Assert.That(File.ReadAllText(ConfigPath), Is.EqualTo(original));
            Assert.That(() => UPilotUserSettings.ClearProjectPorts(ConfigPath), Throws.Exception);
            Assert.That(File.ReadAllText(ConfigPath), Is.EqualTo(original));
        }

        [Test]
        public void ValidComplexJsonRetainsUnknownFieldSemanticsWhenSaving()
        {
            const string original = "{\"custom\":{\"negative\":-12.5,\"exponent\":6.02e+23,\"tiny\":-1E-9,\"text\":\"\\u4e2d\\u6587 \\\"quoted\\\" \\\\ \\/ \\n\\r\\t\",\"nested\":[false,null,{\"key\":\"value\"}]},\"maxSegmentSizeBytes\":1048576,\"maxConcurrentSegmentRequests\":3}";
            WriteConfig(original);
            var settings = UPilotUserSettings.Load(ConfigPath, out var error);
            Assert.That(error, Is.Empty);
            Assert.That(settings.maxSegmentSizeBytes, Is.EqualTo(1048576));
            Assert.That(settings.maxConcurrentSegmentRequests, Is.EqualTo(3));
            Assert.That(UPilotUserSettings.TrySave(ConfigPath, new UPilotUserSettingsData(), out error), Is.True, error);
            AssertFieldPreserved(original, "custom");
            UPilotUserSettings.SetProjectPorts(ConfigPath, ProjectPath("ComplexJson"), 8765, 8011);
            AssertFieldPreserved(original, "custom");
        }

        [TestCase("{\"unknown\":01}")]
        [TestCase("{\"unknown\":1.}")]
        [TestCase("{\"unknown\":1e}")]
        [TestCase("{\"unknown\":--1}")]
        [TestCase("{\"unknown\":0x1}")]
        [TestCase("{\"unknown\":-NaN}")]
        [TestCase("{\"x\":1,}")]
        [TestCase("{\"x\":[1,]}")]
        [TestCase("{\"x\":true false}")]
        public void InvalidJsonSyntaxInUnknownFieldsRejectsReadsAndUpdates(string original)
        {
            WriteConfig(original);
            ExpectFailure("load", ConfigPath);
            AssertDefaults(UPilotUserSettings.Load(ConfigPath, out var loadError));
            Assert.That(loadError, Is.Not.Empty);
            ExpectFailure("save", ConfigPath);
            Assert.That(UPilotUserSettings.TrySave(ConfigPath, new UPilotUserSettingsData(), out var saveError), Is.False);
            Assert.That(saveError, Is.Not.Empty);
            Assert.That(() => UPilotUserSettings.SetProjectPorts(ConfigPath, ProjectPath("Rejected"), 8765, 8011), Throws.Exception);
            Assert.That(File.ReadAllText(ConfigPath), Is.EqualTo(original));
        }

        [Test]
        public void ConcurrentIndependentWritersKeepAllProjectAndDownloadChanges()
        {
            const string original = "{\"custom\":{\"text\":\"keep\"}}";
            WriteConfig(original);
            using var start = new ManualResetEventSlim(false);
            var failures = new ConcurrentQueue<Exception>();
            var writers = Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            {
                try
                {
                    start.Wait();
                    var project = ProjectPath("Writer" + index);
                    // Equivalent path spellings must share the same cross-process lock.
                    var path = index % 2 == 0 ? ConfigPath : Path.Combine(_directory, ".upilot", ".", "settings.json");
                    for (var round = 0; round < 3; round++)
                        UPilotUserSettings.SetProjectPorts(path, project, 10000 + index * 2, 10001 + index * 2);
                }
                catch (Exception ex) { failures.Enqueue(ex); }
            })).ToList();
            writers.Add(Task.Run(() =>
            {
                try
                {
                    start.Wait();
                    for (var round = 0; round < 3; round++)
                        if (!UPilotUserSettings.TrySave(ConfigPath, new UPilotUserSettingsData
                        {
                            maxSegmentSizeBytes = 1048576,
                            maxConcurrentSegmentRequests = 3,
                        }, out var error))
                            throw new InvalidOperationException(error);
                }
                catch (Exception ex) { failures.Enqueue(ex); }
            }));
            start.Set();
            Assert.That(Task.WaitAll(writers.ToArray(), TimeSpan.FromSeconds(30)), Is.True, "Concurrent settings writers did not finish.");
            Assert.That(failures.ToArray(), Is.Empty, string.Join(Environment.NewLine, failures.Select(ex => ex.ToString())));
            var ports = UPilotUserSettings.ReadProjectPorts(ConfigPath);
            Assert.That(ports.Count, Is.EqualTo(8));
            for (var index = 0; index < 8; index++)
                Assert.That(ports[ProjectPath("Writer" + index)], Is.EqualTo(new[] { 10000 + index * 2, 10001 + index * 2 }));
            var settings = UPilotUserSettings.Load(ConfigPath, out var loadError);
            Assert.That(loadError, Is.Empty);
            Assert.That(settings.maxSegmentSizeBytes, Is.EqualTo(1048576));
            Assert.That(settings.maxConcurrentSegmentRequests, Is.EqualTo(3));
            AssertFieldPreserved(original, "custom");
        }

        [Test]
        public void LockIdentityUsesCanonicalConfigurationPath()
        {
            var alias = Path.Combine(_directory, ".upilot", ".", "settings.json");
            Assert.That(UPilotUserSettings.GetLockName(alias), Is.EqualTo(UPilotUserSettings.GetLockName(ConfigPath)));
            Assert.That(UPilotUserSettings.GetLockName(Path.Combine(_directory, "other.json")), Is.Not.EqualTo(UPilotUserSettings.GetLockName(ConfigPath)));
            if (Path.DirectorySeparatorChar == '\\')
                Assert.That(UPilotUserSettings.GetLockName(ConfigPath.ToUpperInvariant()), Is.EqualTo(UPilotUserSettings.GetLockName(ConfigPath)));
        }

        [Test]
        public void RegistryReadFailureThrowsInsteadOfReturningAnEmptyRegistry()
        {
            Directory.CreateDirectory(ConfigPath);
            Assert.That(() => UPilotUserSettings.ReadProjectPorts(ConfigPath), Throws.Exception);
        }

        [Test]
        public void FailedAtomicReplacePreservesExistingSettings()
        {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows file sharing prevents replacing an open destination.");
            const string original = "{\"maxSegmentSizeBytes\":1048576,\"custom\":\"preserve\",\"projectPorts\":{}}";
            WriteConfig(original);
            using (new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                ExpectFailure("save", ConfigPath);
                Assert.That(UPilotUserSettings.TrySave(ConfigPath, new UPilotUserSettingsData(), out var error), Is.False);
                Assert.That(error, Does.Contain(ConfigPath));
                Assert.That(() => UPilotUserSettings.SetProjectPorts(ConfigPath, ProjectPath("Blocked"), 8765, 8011), Throws.Exception);
            }
            Assert.That(File.ReadAllText(ConfigPath), Is.EqualTo(original));
        }

        [Test]
        public void AbandonedSettingsMutexCanBeAcquiredForSaving()
        {
            var lockName = UPilotUserSettings.GetLockName(ConfigPath);
            // Keep the kernel object alive after its previous owning thread exits.
            using var witness = new Mutex(false, lockName);
            Exception threadError = null;
            var owner = new Thread(() =>
            {
                try
                {
                    using var mutex = new Mutex(false, lockName);
                    if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Test could not acquire settings mutex.");
                    // Deliberately omit ReleaseMutex to simulate an interrupted writer.
                }
                catch (Exception ex) { threadError = ex; }
            });
            owner.Start();
            Assert.That(owner.Join(TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(threadError, Is.Null);
            LogAssert.Expect(LogType.Error, new Regex(Regex.Escape($"[UPilotUserSettings] operation=acquire-write-lock; path={ConfigPath}; ") + ".*error="));
            Assert.That(UPilotUserSettings.TrySave(ConfigPath, new UPilotUserSettingsData(), out var error), Is.True, error);
            AssertDefaults(UPilotUserSettings.Load(ConfigPath, out error));
            Assert.That(error, Is.Empty);
        }

        [TestCase("download")]
        [TestCase("ports")]
        [TestCase("clear")]
        public void OtherProcessHoldingSettingsMutexCausesBoundedFailureAndPreservesOriginal(string operation)
        {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows PowerShell verifies a separate process holding the named mutex.");
            const string original = "{\"maxSegmentSizeBytes\":1048576,\"custom\":\"preserve\",\"projectPorts\":{}}";
            WriteConfig(original);
            var scriptPath = Path.Combine(_directory, "hold-settings-mutex.ps1");
            var readyPath = Path.Combine(_directory, "mutex-ready");
            var releasePath = Path.Combine(_directory, "mutex-release");
            File.WriteAllText(scriptPath, @"param([string]$mutexName, [string]$readyPath, [string]$releasePath)
$mutex = New-Object System.Threading.Mutex($false, $mutexName)
$held = $false
try {
    $held = $mutex.WaitOne([TimeSpan]::FromSeconds(10))
    if (-not $held) { throw 'Could not acquire settings mutex.' }
    [IO.File]::WriteAllText($readyPath, 'held')
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not [IO.File]::Exists($releasePath) -and $timer.Elapsed.TotalSeconds -lt 30) {
        [Threading.Thread]::Sleep(20)
    }
} finally {
    if ($held) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}");
            using var holder = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                    Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + scriptPath + "\" \"" +
                        UPilotUserSettings.GetLockName(ConfigPath) + "\" \"" + readyPath + "\" \"" + releasePath + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardError = true,
                },
            };
            var started = false;
            try
            {
                started = holder.Start();
                Assert.That(started, Is.True);
                Assert.That(SpinWait.SpinUntil(() => File.Exists(readyPath) || holder.HasExited, TimeSpan.FromSeconds(10)), Is.True,
                    "Mutex holder did not become ready.");
                Assert.That(File.Exists(readyPath), Is.True, "Mutex holder exited before acquiring the lock.");
                var timer = Stopwatch.StartNew();
                if (operation == "download")
                {
                    ExpectFailure("save", ConfigPath);
                    Assert.That(UPilotUserSettings.TrySave(ConfigPath, new UPilotUserSettingsData(), out var error), Is.False);
                    Assert.That(error, Does.Contain(ConfigPath));
                }
                else if (operation == "ports")
                    Assert.Throws<IOException>(() => UPilotUserSettings.SetProjectPorts(ConfigPath, ProjectPath("Blocked"), 8765, 8011));
                else
                    Assert.Throws<IOException>(() => UPilotUserSettings.ClearProjectPorts(ConfigPath));
                Assert.That(timer.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(4)));
                Assert.That(timer.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
                Assert.That(File.ReadAllText(ConfigPath), Is.EqualTo(original));
            }
            finally
            {
                if (started)
                {
                    File.WriteAllText(releasePath, "release");
                    if (!holder.WaitForExit(10000))
                    {
                        holder.Kill();
                        holder.WaitForExit();
                    }
                }
            }
        }

        private string ProjectPath(string name) => Path.Combine(_directory, "Projects", name).Replace('\\', '/');

        private static string JsonString(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private void AssertFieldPreserved(string original, string name)
        {
            Assert.That(XNode.DeepEquals(ReadJsonField(File.ReadAllText(ConfigPath), name), ReadJsonField(original, name)), Is.True,
                name + " changed while saving a different settings section.");
        }

        private static XElement ReadJsonField(string json, string name)
        {
            using var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json), XmlDictionaryReaderQuotas.Max);
            return XElement.Load(reader).Elements().Single(field => ((string)field.Attribute("item") ?? field.Name.LocalName) == name);
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
