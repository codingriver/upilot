using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CodingRiver.UPilot
{
    [Serializable] public sealed class SceneSummaryMessage { public SceneSummaryRequest payload; }
    [Serializable]
    public sealed class SceneSummaryRequest
    {
        public int maxNodes = 2000;
        public int maxMilliseconds = 100;
        public int maxExamples = 12;
    }

    [Serializable]
    public sealed class SceneSummaryExample
    {
        public string kind;
        public ulong instanceId;
        public string name;
        public string scenePath;
    }

    [Serializable]
    public sealed class SceneSummaryResult
    {
        public int loadedSceneCount;
        public int knownRootCount;
        public int nodesVisited;
        public int activeNodesVisited;
        public int componentsVisited;
        public int componentNodesCompleted;
        public int missingScripts;
        public int cameras;
        public int lights;
        public bool coverageComplete;
        public bool truncated;
        public string truncationReason = "";
        public string countScope = "visited_nodes_only";
        public long elapsedMilliseconds;
        public SceneSummaryRequest budget;
        public List<SceneSummaryExample> examples = new();
    }

    public static class UPilotSceneSummaryService
    {
        public static SceneSummaryResult Summarize(IList<Scene> scenes, SceneSummaryRequest request)
        {
            if (request.maxNodes < 1 || request.maxNodes > 10000 || request.maxMilliseconds < 1 || request.maxMilliseconds > 1000
                || request.maxExamples < 0 || request.maxExamples > 50)
                throw new ArgumentException("Scene summary budget is outside supported bounds.");
            var result = new SceneSummaryResult { budget = request };
            var clock = Stopwatch.StartNew();
            foreach (var scene in scenes)
            {
                if (!scene.isLoaded) continue;
                result.loadedSceneCount++;
                result.knownRootCount += scene.rootCount;
            }
            foreach (var scene in scenes)
            {
                if (!scene.isLoaded) continue;
                if (clock.ElapsedMilliseconds >= request.maxMilliseconds)
                {
                    result.truncationReason = "time_budget";
                    break;
                }
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var node in Traverse(root.transform))
                    {
                        if (result.nodesVisited >= request.maxNodes || clock.ElapsedMilliseconds >= request.maxMilliseconds)
                        {
                            result.truncationReason = result.nodesVisited >= request.maxNodes ? "node_budget" : "time_budget";
                            break;
                        }
                        result.nodesVisited++;
                        if (node.gameObject.activeInHierarchy) result.activeNodesVisited++;
                        var components = node.GetComponents<Component>();
                        foreach (var component in components)
                        {
                            if (clock.ElapsedMilliseconds >= request.maxMilliseconds)
                            {
                                result.truncationReason = "time_budget";
                                break;
                            }
                            result.componentsVisited++;
                            string kind = "";
                            if (component == null) { result.missingScripts++; kind = "missing_script"; }
                            else if (component is Camera) { result.cameras++; kind = "camera"; }
                            else if (component is Light) { result.lights++; kind = "light"; }
                            if (kind != "" && result.examples.Count < request.maxExamples)
                                result.examples.Add(new SceneSummaryExample
                                {
                                    kind = kind, instanceId = UPilotEntityIds.ToWireId(component == null ? node.gameObject : component),
                                    name = Bounded(node.name), scenePath = Bounded(scene.path),
                                });
                        }
                        if (result.truncationReason != "") break;
                        result.componentNodesCompleted++;
                    }
                    if (result.truncationReason != "") break;
                }
                if (result.truncationReason != "") break;
            }
            result.truncated = result.truncationReason != "";
            result.coverageComplete = !result.truncated;
            result.countScope = result.coverageComplete ? "all_loaded_scene_nodes" : "visited_nodes_only";
            result.elapsedMilliseconds = clock.ElapsedMilliseconds;
            return result;
        }

        private static string Bounded(string value) => value.Length <= 256 ? value : value.Substring(0, 256);

        private static IEnumerable<Transform> Traverse(Transform root)
        {
            // Keep only the current ancestor path; wide trees are not materialized.
            var stack = new Stack<(Transform node, int nextChild)>();
            yield return root;
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var frame = stack.Pop();
                if (frame.node == null || frame.nextChild >= frame.node.childCount) continue;
                var child = frame.node.GetChild(frame.nextChild);
                stack.Push((frame.node, frame.nextChild + 1));
                yield return child;
                stack.Push((child, 0));
            }
        }
    }
}
