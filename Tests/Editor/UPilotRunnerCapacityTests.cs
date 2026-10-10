// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using NUnit.Framework.Interfaces;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    /// <summary>
    /// Real Runner capacity probe: 5,001 leaves produce 10,002 start/finish
    /// events, crossing the production retention limit without synthetic callbacks.
    /// Explicit-only; never include this IO-heavy probe in an ordinary suite.
    /// </summary>
    public sealed class UPilotRunnerCapacityTests
    {
        private const int LeafCount = 5001;

        [OneTimeSetUp]
        public void RequireCanonicalProject()
        {
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."))
                .Replace('\\', '/');
            Assert.That(project, Is.EqualTo("D:/upilot/Tests~/UPilotTest").IgnoreCase,
                "Capacity acceptance must not run in a client project.");
        }

        private static IEnumerable Leaves()
        {
            for (int index = 0; index < LeafCount; index++)
                yield return new TestCaseData(index)
                    .SetName("L" + index.ToString("D4", CultureInfo.InvariantCulture));
        }

        [TestCaseSource(nameof(Leaves))]
        [Explicit("V12 targeted real-Runner event retention acceptance only.")]
        public void Leaf(int index)
        {
            Assert.That(index, Is.InRange(0, LeafCount - 1));
        }
    }
}

namespace CodingRiver.UPilot.Tests
{
    // Read-only cost/selection probe over UTF's existing discovery cache. Does not
    // register callbacks, replace a filter, or modify the running Runner's tree.
    public sealed class UPilotRunnerCapacityDiagnosticsTests
    {
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        [Serializable]
        private sealed class Measurement
        {
            public string representation;
            public int visited;
            public int matches;
            public long elapsedMs;
            public long filterBuildMs;
            public double matchedPassMs;
            public double unmatchedPassMs;
            public bool complete;
            public string[] matchedNames;
        }

        [Serializable]
        private sealed class Report
        {
            public string scope = "cached-UPilot.Editor.Tests-leaves-only; not Runner capacity acceptance";
            public int observationBudgetMs = 45000;
            public string unityVersion;
            public string observedAtUtc;
            public int selectedLeaves;
            public int discoveredLeaves;
            public Measurement exactNames;
        }

        [Test]
        [Explicit("Read-only real UTF selector cost diagnostic; not a capacity acceptance run.")]
        public void MeasureExactSelectionAgainstCachedUtfTree()
        {
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/');
            Assert.That(project, Is.EqualTo("D:/upilot/Tests~/UPilotTest").IgnoreCase);
            Type builder = FindType("UnityEngine.TestTools.NUnitExtensions.UnityTestAssemblyBuilder");
            var cache = (IDictionary)builder.GetField("CachedAssemblies", Members).GetValue(null);
            var leaves = new List<ITest>();
            foreach (DictionaryEntry entry in cache)
                if (entry.Value is ITest root && root.Name == "UPilot.Editor.Tests.dll")
                    CollectLeaves(root, leaves);
            var selected = leaves.Where(test => test.FullName.StartsWith(
                "CodingRiver.UPilot.Tests.UPilotRunnerCapacityTests.L", StringComparison.Ordinal)).ToArray();
            Assert.That(selected.Length, Is.EqualTo(5001), "Use the exact capacity fixture already discovered by UTF.");
            var selection = new TestListResultPayload { testMode = "EditMode" };
            selection.selectedTests.AddRange(selected.Select(test => new TestLeafIdentityPayload
                { assembly = "UPilot.Editor.Tests.dll", fullName = test.FullName }));
            Type filterType = FindType("UnityEditor.TestTools.TestRunner.Api.Filter");
            Type modeType = FindType("UnityEditor.TestTools.TestRunner.Api.TestMode");
            object exact = UPilotTestService.CreateSelectionFilters(selection, filterType, modeType).GetValue(0);
            var report = new Report
            {
                unityVersion = Application.unityVersion,
                observedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                selectedLeaves = selected.Length,
                discoveredLeaves = leaves.Count,
                exactNames = Measure(filterType, exact, leaves, "production-exact-names")
            };
            string directory = Path.Combine(project, "Log", "Stage4");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "runner-selector-cost-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json"),
                JsonUtility.ToJson(report, true));
            Assert.That(report.exactNames.complete, Is.True, "Production measurement exceeded its bounded observation budget.");
            var expected = selected.Select(test => test.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Assert.That(report.exactNames.matchedNames, Is.EqualTo(expected));
        }

        [Serializable]
        private sealed class SuiteSelectionReport
        {
            public string scope = "read-only cached UTF filter comparison; not execution or general selector equivalence";
            public int observationBudgetPerMeasurementMs = 45000;
            public string unityVersion;
            public string testFrameworkAssembly;
            public string observedAtUtc;
            public string assemblyName;
            public string fixtureName;
            public string methodName;
            public string[] exactAssemblyNames;
            public string[] fixtureAssemblyNames;
            public string[] methodAssemblyNames;
            public int selectedLeaves;
            public int discoveredLeaves;
            public Measurement exactPass;
            public Measurement fixturePass;
            public Measurement exactExplicit;
            public Measurement fixtureExplicit;
            public Measurement methodPass;
            public Measurement methodExplicit;
            public string[] expectedNames;
            public bool cachedLeafStateUnchanged;
            public string[] selectedLeafRunStates;
            public AncestorComparison[] selectedAncestors;
        }

        [Serializable]
        private sealed class AncestorComparison
        {
            public string name;
            public string runState;
            public bool exactPass;
            public bool fixturePass;
            public bool exactExplicit;
            public bool fixtureExplicit;
            public bool methodPass;
            public bool methodExplicit;
        }

        [Test]
        [Explicit("Read-only complete-method selector hypothesis and fixture counterexample; never executes capacity leaves.")]
        public void CompareCompleteMethodSelectionAndRejectFixtureExplicitMismatch()
        {
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/');
            Assert.That(project, Is.EqualTo("D:/upilot/Tests~/UPilotTest").IgnoreCase);
            const string assemblyName = "UPilot.Editor.Tests.dll";
            const string fixtureName = "CodingRiver.UPilot.Tests.UPilotRunnerCapacityTests";
            Type builder = FindType("UnityEngine.TestTools.NUnitExtensions.UnityTestAssemblyBuilder");
            var cache = (IDictionary)builder.GetField("CachedAssemblies", Members).GetValue(null);
            var leaves = new List<ITest>();
            foreach (DictionaryEntry entry in cache)
                if (entry.Value is ITest root && root.Name == assemblyName)
                    CollectLeaves(root, leaves);
            // Resolve the actual ancestor, not a name prefix which could include a neighbouring fixture.
            var selected = leaves.Where(leaf => HasAncestor(leaf, fixtureName)).ToArray();
            Assert.That(selected.Length, Is.EqualTo(5001));
            // Explicit can reside on an ancestor (e.g. the parameterized method), not each leaf.
            var ancestors = new HashSet<ITest>();
            foreach (var leaf in selected)
                for (ITest parent = leaf.Parent; parent != null; parent = parent.Parent) ancestors.Add(parent);
            Assert.That(selected.All(leaf => leaf.RunState == RunState.Runnable), Is.True,
                "This diagnostic covers the observed runnable leaves under an Explicit parameterized method.");
            ITest methodNode = selected[0].Parent;
            Assert.That(methodNode.RunState, Is.EqualTo(RunState.Explicit));
            Assert.That(selected.All(leaf => ReferenceEquals(leaf.Parent, methodNode)), Is.True);
            var observedNodes = leaves.Concat(ancestors).ToArray();
            string[] before = observedNodes.Select(leaf => leaf.Id + "|" + leaf.FullName + "|" + leaf.RunState).ToArray();
            var selection = new TestListResultPayload { testMode = "EditMode" };
            selection.selectedTests.AddRange(selected.Select(leaf => new TestLeafIdentityPayload
                { assembly = assemblyName, fullName = leaf.FullName }));
            Type filterType = FindType("UnityEditor.TestTools.TestRunner.Api.Filter");
            Type modeType = FindType("UnityEditor.TestTools.TestRunner.Api.TestMode");
            object exact = UPilotTestService.CreateSelectionFilters(selection, filterType, modeType).GetValue(0);
            object fixture = Activator.CreateInstance(filterType);
            filterType.GetField("testMode").SetValue(fixture, Enum.Parse(modeType, "EditMode"));
            // Match the production-normalized assembly selector; only testNames may differ.
            var assemblySelectors = (string[])filterType.GetField("assemblyNames").GetValue(exact);
            filterType.GetField("assemblyNames").SetValue(fixture, (string[])assemblySelectors.Clone());
            filterType.GetField("testNames").SetValue(fixture, new[] { fixtureName });
            object method = Activator.CreateInstance(filterType);
            filterType.GetField("testMode").SetValue(method, Enum.Parse(modeType, "EditMode"));
            filterType.GetField("assemblyNames").SetValue(method, (string[])assemblySelectors.Clone());
            filterType.GetField("testNames").SetValue(method, new[] { methodNode.FullName });
            var report = new SuiteSelectionReport
            {
                unityVersion = Application.unityVersion,
                testFrameworkAssembly = filterType.Assembly.FullName,
                observedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                assemblyName = assemblyName,
                fixtureName = fixtureName,
                methodName = methodNode.FullName,
                exactAssemblyNames = assemblySelectors,
                fixtureAssemblyNames = (string[])filterType.GetField("assemblyNames").GetValue(fixture),
                methodAssemblyNames = (string[])filterType.GetField("assemblyNames").GetValue(method),
                selectedLeaves = selected.Length,
                discoveredLeaves = leaves.Count,
                selectedLeafRunStates = selected.GroupBy(leaf => leaf.RunState).OrderBy(group => group.Key)
                    .Select(group => group.Key + ":" + group.Count()).ToArray(),
                expectedNames = selected.Select(leaf => leaf.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray()
            };
            string path = Path.Combine(project, "Log", "Stage4", "runner-suite-selector-" +
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var exactCompiled = CompileFilter(filterType, exact);
                var fixtureCompiled = CompileFilter(filterType, fixture);
                var methodCompiled = CompileFilter(filterType, method);
                report.selectedAncestors = ancestors.OrderBy(node => node.FullName, StringComparer.Ordinal)
                    .Select(node => new AncestorComparison
                    {
                        name = node.FullName, runState = node.RunState.ToString(),
                        exactPass = exactCompiled.Pass(node), fixturePass = fixtureCompiled.Pass(node),
                        exactExplicit = exactCompiled.IsExplicitMatch(node), fixtureExplicit = fixtureCompiled.IsExplicitMatch(node),
                        methodPass = methodCompiled.Pass(node), methodExplicit = methodCompiled.IsExplicitMatch(node)
                    }).ToArray();
                // Measure the new hypothesis first; preserve it even if the baseline exceeds its budget.
                report.fixturePass = Measure(filterType, fixture, leaves, "fixture-fullname:Pass");
                report.fixtureExplicit = Measure(filterType, fixture, leaves, "fixture-fullname:IsExplicitMatch", true);
                report.methodPass = Measure(filterType, method, leaves, "method-fullname:Pass");
                report.methodExplicit = Measure(filterType, method, leaves, "method-fullname:IsExplicitMatch", true);
                report.exactPass = Measure(filterType, exact, leaves, "production-exact:Pass");
                report.exactExplicit = Measure(filterType, exact, leaves, "production-exact:IsExplicitMatch", true);
            }
            finally
            {
                report.cachedLeafStateUnchanged = before.SequenceEqual(observedNodes.Select(
                    leaf => leaf.Id + "|" + leaf.FullName + "|" + leaf.RunState));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(report, true));
            }
            Assert.That(report.cachedLeafStateUnchanged, Is.True);
            Assert.That(report.fixtureAssemblyNames, Is.EqualTo(report.exactAssemblyNames));
            Assert.That(report.methodAssemblyNames, Is.EqualTo(report.exactAssemblyNames));
            foreach (var ancestor in report.selectedAncestors)
            {
                Assert.That(ancestor.fixturePass, Is.EqualTo(ancestor.exactPass), ancestor.name + ":fixture Pass");
                Assert.That(ancestor.methodPass, Is.EqualTo(ancestor.exactPass), ancestor.name + ":method Pass");
                if (ancestor.runState == RunState.Explicit.ToString())
                {
                    Assert.That(ancestor.exactExplicit, Is.True, ancestor.name + ":baseline Explicit");
                    Assert.That(ancestor.fixtureExplicit, Is.False, ancestor.name + ":fixture is not an equivalent selector");
                    Assert.That(ancestor.methodExplicit, Is.True, ancestor.name + ":method must preserve the Explicit gate");
                }
            }
            foreach (var measurement in new[] { report.fixturePass, report.fixtureExplicit, report.methodPass,
                report.methodExplicit, report.exactPass, report.exactExplicit })
                Assert.That(measurement.complete, Is.True, measurement.representation + " exceeded the observation budget.");
            foreach (var measurement in new[] { report.fixturePass, report.methodPass, report.exactPass, report.exactExplicit })
                Assert.That(measurement.matchedNames, Is.EqualTo(report.expectedNames), measurement.representation);
            // UTF TestCommandBuilder only tests IsExplicitMatch for an Explicit leaf. These leaves
            // are Runnable; CompositeWorkItem applies the necessary gate to the Explicit parent above.
            Assert.That(report.fixtureExplicit.matches, Is.Zero);
            Assert.That(report.methodExplicit.matches, Is.Zero);
        }

        [Serializable]
        private sealed class ProductionMethodReport
        {
            public string scope = "actual UTF adaptor discovery and production filter; not capacity execution";
            public string observedAtUtc;
            public string selectionDomain;
            public string selectionSnapshotId;
            public int selectedLeaves;
            public string[] runnerNames;
            public string[] runnerAssemblies;
            public Measurement pass;
            public bool explicitAncestorsMatched;
            public bool cachedTreeUnchanged;
            public bool partialSelectionRemainedExact;
        }

        [Test]
        [Explicit("Verify production complete-method selection against actual UTF discovery; does not execute capacity leaves.")]
        public void ProductionCompleteMethodMatchesActualUtfTreeWithoutBroadening()
        {
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/');
            Assert.That(project, Is.EqualTo("D:/upilot/Tests~/UPilotTest").IgnoreCase);
            var cache = (IDictionary)FindType("UnityEngine.TestTools.NUnitExtensions.UnityTestAssemblyBuilder")
                .GetField("CachedAssemblies", Members).GetValue(null);
            ITest root = cache.Values.Cast<object>().OfType<ITest>().Single(node => node.Name == "UPilot.Editor.Tests.dll");
            var leaves = new List<ITest>();
            CollectLeaves(root, leaves);
            const string fixtureName = "CodingRiver.UPilot.Tests.UPilotRunnerCapacityTests";
            var selected = leaves.Where(test => HasAncestor(test, fixtureName)).ToArray();
            Assert.That(selected.Length, Is.EqualTo(5001));
            var ancestors = new HashSet<ITest>();
            foreach (var leaf in selected)
                for (var parent = leaf.Parent; parent != null; parent = parent.Parent) ancestors.Add(parent);
            var observed = leaves.Concat(ancestors).Distinct().ToArray();
            string[] before = observed.Select(node => node.Id + "|" + node.FullName + "|" + node.RunState).ToArray();
            Type factoryType = FindType("UnityEditor.TestTools.TestRunner.Api.TestAdaptorFactory");
            var adaptor = factoryType.GetMethod("Create", new[] { typeof(ITest) })
                .Invoke(Activator.CreateInstance(factoryType, true), new object[] { root });
            var selection = UPilotTestService.ResolveTestSelection(adaptor, "EditMode", "", fixtures: new[] { fixtureName });
            Assert.That(selection.matchedCount, Is.EqualTo(5001));
            Type filterType = FindType("UnityEditor.TestTools.TestRunner.Api.Filter");
            Type modeType = FindType("UnityEditor.TestTools.TestRunner.Api.TestMode");
            object filter = UPilotTestService.CreateSelectionFilters(selection, filterType, modeType).GetValue(0);
            var report = new ProductionMethodReport
            {
                observedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                selectionDomain = selection.selectionDomain, selectionSnapshotId = selection.selectionSnapshotId,
                selectedLeaves = selection.matchedCount,
                runnerNames = (string[])filterType.GetField("testNames").GetValue(filter),
                runnerAssemblies = (string[])filterType.GetField("assemblyNames").GetValue(filter)
            };
            string path = Path.Combine(project, "Log", "Stage4", "runner-production-method-" +
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                // Fail before a potentially expensive traversal if discovery did not prove compression.
                Assert.That(report.runnerNames, Is.EqualTo(new[] { fixtureName + ".Leaf" }));
                Assert.That(report.runnerAssemblies, Is.EqualTo(new[] { "UPilot.Editor.Tests" }));
                var compiled = CompileFilter(filterType, filter);
                report.explicitAncestorsMatched = ancestors.All(node => compiled.Pass(node)
                    && (node.RunState != RunState.Explicit || compiled.IsExplicitMatch(node)));
                report.pass = Measure(filterType, filter, leaves, "production-complete-method:Pass");
                var partial = UPilotTestService.ResolveTestSelection(adaptor, "EditMode", "",
                    testNames: selected.Take(2).Select(node => node.FullName).ToArray());
                var partialFilter = UPilotTestService.CreateSelectionFilters(partial, filterType, modeType).GetValue(0);
                report.partialSelectionRemainedExact = ((string[])filterType.GetField("testNames").GetValue(partialFilter))
                    .SequenceEqual(partial.tests);
            }
            finally
            {
                report.cachedTreeUnchanged = before.SequenceEqual(observed.Select(node => node.Id + "|" + node.FullName + "|" + node.RunState));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(report, true));
            }
            Assert.That(report.pass.complete, Is.True);
            Assert.That(report.pass.matchedNames, Is.EqualTo(selected.Select(node => node.FullName).OrderBy(name => name, StringComparer.Ordinal)));
            Assert.That(report.explicitAncestorsMatched, Is.True);
            Assert.That(report.partialSelectionRemainedExact, Is.True);
            Assert.That(report.cachedTreeUnchanged, Is.True);
        }

        private static bool HasAncestor(ITest leaf, string fullName)
        {
            for (ITest parent = leaf.Parent; parent != null; parent = parent.Parent)
                if (string.Equals(parent.FullName, fullName, StringComparison.Ordinal)) return true;
            return false;
        }

        private static Measurement Measure(Type filterType, object filter, List<ITest> leaves, string representation, bool explicitMatch = false)
        {
            var clock = Stopwatch.StartNew();
            var compiled = CompileFilter(filterType, filter);
            var result = new Measurement { representation = representation, filterBuildMs = clock.ElapsedMilliseconds };
            long matchedTicks = 0;
            long unmatchedTicks = 0;
            var matched = new List<string>();
            foreach (var leaf in leaves)
            {
                if (clock.ElapsedMilliseconds >= 45000) break;
                long started = clock.ElapsedTicks;
                bool accepted = explicitMatch ? compiled.IsExplicitMatch(leaf) : compiled.Pass(leaf);
                long passTicks = clock.ElapsedTicks - started;
                if (accepted)
                {
                    matched.Add(leaf.FullName);
                    matchedTicks += passTicks;
                }
                else unmatchedTicks += passTicks;
                result.visited++;
            }
            result.elapsedMs = clock.ElapsedMilliseconds;
            result.matchedPassMs = matchedTicks * 1000.0 / Stopwatch.Frequency;
            result.unmatchedPassMs = unmatchedTicks * 1000.0 / Stopwatch.Frequency;
            result.matches = matched.Count;
            result.complete = result.visited == leaves.Count;
            result.matchedNames = matched.OrderBy(name => name, StringComparer.Ordinal).ToArray();
            return result;
        }

        private static ITestFilter CompileFilter(Type filterType, object filter)
        {
            object runtime = filterType.GetMethod("ToRuntimeTestRunnerFilter", Members).Invoke(filter, new object[] { false });
            return (ITestFilter)runtime.GetType().GetMethod("BuildNUnitFilter", Members).Invoke(runtime, null);
        }

        private static void CollectLeaves(ITest test, List<ITest> leaves)
        {
            if (!test.IsSuite) { leaves.Add(test); return; }
            foreach (var child in test.Tests) CollectLeaves(child, leaves);
        }

        private static Type FindType(string name)
        {
            return AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name))
                .First(type => type != null);
        }
    }
}

namespace CodingRiver.UPilot.Tests
{
    // Small real execution control before the IO-heavy capacity probe.
    public sealed class UPilotRunnerMethodSelectionAcceptanceTests
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [Explicit("V12 complete-method real execution control only.")]
        public void Leaf(int index)
        {
            Assert.That(Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/'),
                Is.EqualTo("D:/upilot/Tests~/UPilotTest").IgnoreCase);
            Assert.That(index, Is.InRange(0, 2));
        }
    }
}
