// -----------------------------------------------------------------------
// UPilot Editor test fixture for Operation/reflection acceptance.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace CodingRiver.UPilot.Tests
{
    public static class UPilotOperationReflectionFixture
    {
        private const string StartCallCountKey = "UPilot.OperationReflectionFixture.StartCallCount";
        private const string CancelCallCountKey = "UPilot.OperationReflectionFixture.CancelCallCount";
        private const string CanceledOperationKey = "UPilot.OperationReflectionFixture.CanceledOperation";
        private static int s_statusCalls;
        private static GameObject s_navMeshRoot;
        private static Component s_navMeshSurface;
        private static NavMeshData s_navMeshData;

        public static string Start()
        {
            RecordStartCall();
            SessionState.SetString(CanceledOperationKey, "");
            s_statusCalls = 0;
            return "{\"status\":\"Running\",\"phase\":\"Started\",\"operationId\":\"fixture-operation\"}";
        }

        public static string StartWithDelay(string milliseconds)
        {
            int count = RecordStartCall();
            SessionState.SetString(CanceledOperationKey, "");
            s_statusCalls = 0;
            Thread.Sleep(int.Parse(milliseconds));
            return $"{{\"status\":\"Running\",\"phase\":\"Started\",\"operationId\":\"fixture-operation-{count}\"}}";
        }

        public static string ResetStartCallCount()
        {
            SessionState.SetInt(StartCallCountKey, 0);
            SessionState.SetInt(CancelCallCountKey, 0);
            SessionState.SetString(CanceledOperationKey, "");
            return "{\"startCallCount\":0,\"cancelCallCount\":0}";
        }

        public static string GetStartCallCount()
        {
            return $"{{\"startCallCount\":{SessionState.GetInt(StartCallCountKey, 0)}}}";
        }

        public static string GetCancelCallCount()
        {
            return $"{{\"cancelCallCount\":{SessionState.GetInt(CancelCallCountKey, 0)}}}";
        }

        public static string StatusForOperation(string operationId)
        {
            if (SessionState.GetString(CanceledOperationKey, "") == operationId)
                return $"{{\"status\":\"Canceled\",\"phase\":\"Canceled\",\"operationId\":\"{operationId}\",\"cleanupPending\":false}}";
            s_statusCalls++;
            return s_statusCalls < 2
                ? $"{{\"status\":\"Running\",\"phase\":\"Working\",\"operationId\":\"{operationId}\",\"progress\":0.5}}"
                : $"{{\"status\":\"Succeeded\",\"phase\":\"Complete\",\"operationId\":\"{operationId}\",\"progress\":1.0,\"cleanupPending\":false}}";
        }

        public static string StatusForOperationWithDelay(string operationId, string milliseconds)
        {
            Thread.Sleep(int.Parse(milliseconds));
            return StatusForOperation(operationId);
        }

        public static string StartForProcessExit(string operationId)
        {
            string directory = GetProcessExitDirectory(operationId);
            Directory.CreateDirectory(directory);
            string relativePath = $"Log/P0P1/OperationProcessExit/{operationId}/start.json";
            string json = $"{{\"operationId\":\"{operationId}\",\"invocationCount\":1," +
                          $"\"unityProcessId\":{System.Diagnostics.Process.GetCurrentProcess().Id}," +
                          $"\"startedAt\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}";
            using (var stream = new FileStream(Path.Combine(directory, "start.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream))
                writer.Write(json);
            return $"{{\"status\":\"Running\",\"phase\":\"Started\",\"operationId\":\"{operationId}\"," +
                   $"\"startEvidencePath\":\"{relativePath}\"}}";
        }

        public static string StatusForProcessExit(string operationId)
        {
            string directory = GetProcessExitDirectory(operationId);
            string startPath = Path.Combine(directory, "start.json");
            if (!File.Exists(startPath))
                return $"{{\"status\":\"Failed\",\"phase\":\"IdentityMissing\",\"operationId\":\"{operationId}\"," +
                       "\"failureSignature\":\"OperationFixtureIdentityMissing\",\"cleanupPending\":true}}";

            string countPath = Path.Combine(directory, "status-count.txt");
            int count = File.Exists(countPath) ? int.Parse(File.ReadAllText(countPath)) : 0;
            count++;
            File.WriteAllText(countPath, count.ToString());
            string status = count < 2 ? "Running" : "Succeeded";
            string phase = count < 2 ? "RecoveredObservation" : "Complete";
            return $"{{\"status\":\"{status}\",\"phase\":\"{phase}\",\"operationId\":\"{operationId}\"," +
                   $"\"statusCallCount\":{count},\"cleanupPending\":false}}";
        }

        public static string GetProcessExitStartEvidence(string operationId)
        {
            return File.ReadAllText(Path.Combine(GetProcessExitDirectory(operationId), "start.json"));
        }

        private static string GetProcessExitDirectory(string operationId)
        {
            if (string.IsNullOrEmpty(operationId) || operationId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("operationId must be a safe non-empty file name.", nameof(operationId));
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, "Log", "P0P1", "OperationProcessExit", operationId);
        }

        public static string StatusWithCleanupPending(string operationId)
        {
            return $"{{\"status\":\"Succeeded\",\"phase\":\"Cleanup\",\"operationId\":\"{operationId}\",\"cleanupPending\":true}}";
        }

        private static int RecordStartCall()
        {
            int count = SessionState.GetInt(StartCallCountKey, 0) + 1;
            SessionState.SetInt(StartCallCountKey, count);
            return count;
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

        public static string CancelWithDelay(string operationId, string milliseconds)
        {
            int count = SessionState.GetInt(CancelCallCountKey, 0) + 1;
            SessionState.SetInt(CancelCallCountKey, count);
            SessionState.SetString(CanceledOperationKey, operationId);
            Thread.Sleep(int.Parse(milliseconds));
            return $"{{\"status\":\"Canceled\",\"phase\":\"Canceled\",\"operationId\":\"{operationId}\",\"cleanupPending\":false}}";
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
