// -----------------------------------------------------------------------
// UPilot Editor test fixture for Operation/reflection acceptance.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using UnityEngine;
using UnityEngine.AI;

namespace CodingRiver.UPilot.Tests
{
    public static class UPilotOperationReflectionFixture
    {
        private static int s_statusCalls;
        private static GameObject s_navMeshRoot;
        private static Component s_navMeshSurface;
        private static NavMeshData s_navMeshData;

        public static string Start()
        {
            s_statusCalls = 0;
            return "{\"status\":\"Running\",\"phase\":\"Started\",\"operationId\":\"fixture-operation\"}";
        }

        public static string Status()
        {
            s_statusCalls++;
            return s_statusCalls < 2
                ? "{\"status\":\"Running\",\"phase\":\"Working\",\"progress\":0.5}"
                : "{\"status\":\"Succeeded\",\"phase\":\"Complete\",\"progress\":1.0}";
        }

        public static string Cancel()
        {
            return "{\"status\":\"Canceled\",\"phase\":\"Canceled\",\"cleanupPending\":false}";
        }

        public static string LongCall(string milliseconds)
        {
            Thread.Sleep(int.Parse(milliseconds));
            return "long-call-complete";
        }

        public static string EmitConsoleLogs(string countText, string payloadLengthText)
        {
            int count = Math.Max(1, Math.Min(10000, int.Parse(countText)));
            int payloadLength = Math.Max(1, Math.Min(4096, int.Parse(payloadLengthText)));
            string payload = new string('x', payloadLength);
            for (int i = 0; i < count; i++)
                Debug.Log($"UPilotLargeCapture:{i:D5}:{payload}");
            return $"{{\"emitted\":{count},\"payloadLength\":{payloadLength}}}";
        }

        public static string SampleProfilerTelemetry()
        {
            return $"{{\"fixture\":\"UPilotTest\",\"frame\":{Time.frameCount},\"playing\":{(Application.isPlaying ? "true" : "false")}}}";
        }

        public static string CreateNavMeshFixture()
        {
            DestroyNavMeshFixture();
            var settings = NavMesh.GetSettingsByIndex(0);
            var sources = new List<NavMeshBuildSource>
            {
                new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Box,
                    size = new Vector3(20f, 0.2f, 20f),
                    transform = Matrix4x4.TRS(new Vector3(0f, -0.1f, 0f), Quaternion.identity, Vector3.one),
                    area = 0,
                },
            };
            s_navMeshData = NavMeshBuilder.BuildNavMeshData(
                settings,
                sources,
                new Bounds(Vector3.zero, new Vector3(24f, 4f, 24f)),
                Vector3.zero,
                Quaternion.identity);
            if (s_navMeshData == null)
                throw new InvalidOperationException("Failed to build the UPilot NavMesh fixture data.");

            s_navMeshRoot = new GameObject("__UPilotNavMeshFixture");
            s_navMeshRoot.transform.position = new Vector3(100f, 0f, 50f);
            Type surfaceType = Type.GetType("Unity.AI.Navigation.NavMeshSurface, Unity.AI.Navigation", true);
            s_navMeshSurface = s_navMeshRoot.AddComponent(surfaceType);
            surfaceType.GetProperty("navMeshData", BindingFlags.Public | BindingFlags.Instance)
                ?.SetValue(s_navMeshSurface, s_navMeshData);
            surfaceType.GetMethod("AddData", BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(s_navMeshSurface, null);

            var agentObject = new GameObject("Agent");
            agentObject.transform.SetParent(s_navMeshRoot.transform, false);
            var agent = agentObject.AddComponent<NavMeshAgent>();
            agent.agentTypeID = settings.agentTypeID;
            agent.Warp(s_navMeshRoot.transform.position);
            return $"{{\"created\":true,\"agentTypeId\":{settings.agentTypeID},\"x\":100,\"z\":50}}";
        }

        public static string MoveNavMeshFixture(string xText, string zText)
        {
            if (s_navMeshRoot == null)
                throw new InvalidOperationException("NavMesh fixture has not been created.");
            s_navMeshRoot.transform.position = new Vector3(float.Parse(xText), 0f, float.Parse(zText));
            return $"{{\"moved\":true,\"x\":{xText},\"z\":{zText}}}";
        }

        public static string DestroyNavMeshFixture()
        {
            if (s_navMeshRoot != null)
                UnityEngine.Object.DestroyImmediate(s_navMeshRoot);
            if (s_navMeshData != null)
                UnityEngine.Object.DestroyImmediate(s_navMeshData);
            s_navMeshRoot = null;
            s_navMeshSurface = null;
            s_navMeshData = null;
            return "{\"destroyed\":true}";
        }
    }
}
