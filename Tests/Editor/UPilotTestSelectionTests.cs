using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public class UPilotTestSelectionTests
    {
        public class TypeNode { public string FullName { get; set; } }
        public class Node
        {
            public string FullName { get; set; }
            public string Name { get; set; }
            public bool IsSuite { get; set; }
            public bool IsTestAssembly { get; set; }
            public TypeNode TypeInfo { get; set; }
            public IEnumerable<Node> Children { get; set; } = Array.Empty<Node>();
        }

        private static Node Tree() => new Node
        {
            IsSuite = true, IsTestAssembly = true, Name = "Fixture.dll",
            Children = new[]
            {
                new Node { IsSuite = true, TypeInfo = new TypeNode { FullName = "Demo.First" }, Children = new[]
                {
                    new Node { FullName = "Demo.First.One" },
                    new Node { FullName = "Demo.First.Cases(\"a.b\")" },
                } },
                new Node { IsSuite = true, TypeInfo = new TypeNode { FullName = "Demo.Second" }, Children = new[]
                {
                    new Node { FullName = "Demo.Second.Two" },
                } },
            },
        };

        [Test]
        public void ExactSelectorsUnionDeduplicatesAndReportsEachMatch()
        {
            var result = UPilotTestService.ResolveTestSelection(Tree(), "EditMode", "",
                new[] { "Demo.First.One", "Demo.Second.Two", "Missing" },
                new[] { "Demo.First", "Demo.First" });
            Assert.That(result.tests, Has.Count.EqualTo(3));
            Assert.That(result.selectors.ConvertAll(item => item.matchedCount), Is.EqualTo(new[] { 1, 1, 0, 2, 2 }));
            Assert.That(result.assemblies, Is.EqualTo(new[] { "Fixture.dll" }));
        }

        [Test]
        public void NoMatchesNeverSelectsWholeTree()
        {
            var result = UPilotTestService.ResolveTestSelection(Tree(), "EditMode", "", new[] { "Missing" });
            Assert.That(result.matchedCount, Is.Zero);
            Assert.That(result.discoveryStatus, Is.EqualTo("filter_no_match"));
        }

        [Test]
        public void DuplicateFullNamesCannotSilentlyRunMoreTestsThanDiscoveredSelection()
        {
            var root = new Node { IsSuite = true, Children = new[]
            {
                new Node { FullName = "Same.Fixture.Test" },
                new Node { FullName = "Same.Fixture.Test" },
            } };
            Assert.Throws<ArgumentException>(() => UPilotTestService.ResolveTestSelection(
                root, "EditMode", "", new[] { "Same.Fixture.Test" }));
        }

        [Test]
        public void EmptyAndMixedSelectorsAreRejected()
        {
            Assert.Throws<ArgumentException>(() => UPilotTestService.ValidateSelectors("", Array.Empty<string>(), null));
            Assert.Throws<ArgumentException>(() => UPilotTestService.ValidateSelectors("regex:.*", new[] { "Demo.First.One" }, null));
            Assert.Throws<ArgumentException>(() => UPilotTestService.ValidateSelectors("", null, new[] { "" }));
        }

        [Test]
        public void LegacyFixtureListUsesTreeTypeNotParameterizedNameSplitting()
        {
            var result = UPilotTestService.ResolveTestSelection(Tree(), "EditMode", "Demo.First");
            Assert.That(result.matchedCount, Is.EqualTo(2));
            Assert.That(result.tests, Does.Contain("Demo.First.Cases(\"a.b\")"));
        }

        [UnityTest]
        public IEnumerator NetworkObservationsStayOnDiskDuringActiveRun()
        {
            if (UPilotTestService.Instance?.IsRunning != true)
                Assert.Ignore("Requires an UPilot-started run to exercise active observation isolation.");
            bool previous = Logger.LogToUnityConsole;
            var level = Logger.MinLevel;
            string marker = "upilot-network-isolation-" + Guid.NewGuid().ToString("N");
            var consoleMessages = new List<string>();
            UnityEngine.Application.LogCallback callback = (message, stack, type) => consoleMessages.Add(message);
            UnityEngine.Application.logMessageReceived += callback;
            try
            {
                Logger.LogToUnityConsole = true;
                Logger.MinLevel = Logger.LogLevel.Info;
                for (int index = 0; index < 20; index++)
                {
                    Logger.LogNetwork("NETWORK", marker, false);
                    Logger.LogNetwork(marker, true);
                    Logger.Log("COMMAND", marker);
                    Logger.Log("NETWORK", marker);
                    yield return null;
                }
                Assert.That(consoleMessages, Has.None.Contains(marker));
                Assert.That(File.ReadAllText(Logger.LogFilePath), Does.Contain(marker));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                UnityEngine.Application.logMessageReceived -= callback;
                Logger.LogToUnityConsole = previous;
                Logger.MinLevel = level;
            }
        }
    }
}
