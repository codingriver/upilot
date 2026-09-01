// -----------------------------------------------------------------------
// UPilot Editor — runtime NavMesh and Profiler diagnostics
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace CodingRiver.UPilot
{
    [Serializable] public class NavMeshStatusMessage { public NavMeshStatusPayload payload; }
    [Serializable] public class NavMeshStatusPayload { public bool includeSurfaces = true; public bool includeAgents = true; public bool includeTriangulation = true; }
    [Serializable] public class NavMeshSampleMessage { public NavMeshSamplePayload payload; }
    [Serializable] public class NavMeshSamplePayload { public List<NavMeshPointPayload> points = new(); public float maxDistance = 10f; public int areaMask = NavMesh.AllAreas; public int agentTypeId = -1; }
    [Serializable] public class NavMeshPointPayload { public float x; public float y; public float z; }
    [Serializable] public class NavMeshVectorPayload { public float x; public float y; public float z; public NavMeshVectorPayload() { } public NavMeshVectorPayload(Vector3 value) { x = value.x; y = value.y; z = value.z; } }
    [Serializable] public class NavMeshBoundsPayload { public NavMeshVectorPayload center; public NavMeshVectorPayload size; }
    [Serializable] public class NavMeshSurfaceInfoPayload
    {
        public string gameObjectPath;
        public ulong instanceId;
        public bool enabled;
        public bool activeInHierarchy;
        public string componentType;
        public string navMeshDataName;
        public int navMeshDataInstanceId;
        public bool navMeshDataInstanceValid;
        public int agentTypeId;
        public int sourceLayerMask;
        public int defaultArea;
        public NavMeshVectorPayload transformPosition;
        public NavMeshVectorPayload transformEulerAngles;
        public NavMeshVectorPayload transformLossyScale;
        public NavMeshBoundsPayload sourceBounds;
        public NavMeshBoundsPayload inferredWorldBounds;
        public string registrationMatrixSource = "surfaceTransform-inferred";
        public bool registrationTransformDirectlyObservable;
        public string registrationState;
        public long surfaceTransformVersion;
        public long transformChangedAt;
        public long transformObservedPreUpdateVersion;
        public bool transformAwaitingPreUpdate;
    }
    [Serializable] public class NavMeshAgentSummaryPayload { public int total; public int enabled; public int active; public int onNavMesh; public int offNavMesh; }
    [Serializable] public class NavMeshTriangulationPayload { public int vertexCount; public int triangleCount; public int areaCount; public NavMeshBoundsPayload worldBounds; }
    [Serializable] public class NavMeshStatusResultPayload
    {
        public bool ok = true;
        public long observedAt;
        public long lastPreUpdateAt;
        public long preUpdateVersion;
        public long observationVersion;
        public string observationSignature;
        public List<NavMeshSurfaceInfoPayload> surfaces = new();
        public NavMeshAgentSummaryPayload agents;
        public NavMeshTriangulationPayload triangulation;
        public string limitation = "Unity public APIs do not expose the registered NavMeshDataInstance matrix; inferredWorldBounds uses the current Surface Transform and is explicitly marked inferred.";
    }
    [Serializable] public class NavMeshSampleItemPayload
    {
        public NavMeshVectorPayload requested;
        public bool found;
        public NavMeshVectorPayload hitPosition;
        public float distance;
        public int mask;
        public bool edgeFound;
        public NavMeshVectorPayload edgePosition;
        public float edgeDistance;
        public string matchingSurfacePath;
        public string matchingConfidence;
        public int matchingNavMeshDataInstanceId;
        public int matchingAgentTypeId;
        public string registrationMatrixSource;
    }
    [Serializable] public class NavMeshSampleResultPayload { public bool ok = true; public float maxDistance; public int areaMask; public int agentTypeId; public List<NavMeshSampleItemPayload> samples = new(); }

    [Serializable] public class ProfilerCaptureStartMessage { public ProfilerCaptureStartPayload payload; }
    [Serializable] public class ProfilerCaptureStartPayload
    {
        public float durationSec = 30f;
        public int sampleEveryFrames = 1;
        public string title = "runtime-profiler";
        public string outputDirectory = "";
        public string[] markerNames;
        public string markerNameRegex = "";
        public int maxMarkers = 64;
        public string telemetryTypeName = "";
        public string telemetryMethodName = "";
        public string baselineJsonPath = "";
        public bool includeDefaultAiMarkers = true;
    }
    [Serializable] public class ProfilerCaptureStatusMessage { public ProfilerCaptureStatusPayload payload; }
    [Serializable] public class ProfilerCaptureStatusPayload { public string captureId = ""; }
    [Serializable] public class ProfilerCaptureSamplePayload
    {
        public int frame;
        public double elapsedSec;
        public double mainThreadMs;
        public double renderThreadMs;
        public double gpuFrameMs;
        public long gcAllocatedBytes;
        public long gcCollectionCount;
        public long managedHeapUsedBytes;
        public long managedHeapReservedBytes;
        public long drawCalls;
        public long setPassCalls;
        public long batches;
        public long triangles;
        public long vertices;
        public int navMeshAgents;
        public int animators;
        public int skinnedMeshRenderers;
        public int particleSystems;
        public List<ProfilerMarkerValuePayload> markers = new();
        public string telemetryJson;
    }
    [Serializable] public class ProfilerMetricSummaryPayload { public string name; public string unit; public double p50; public double p95; public double p99; public double max; public double average; }
    [Serializable] public class ProfilerMarkerValuePayload { public string name; public string unit; public double value; }
    [Serializable] public class ProfilerMarkerSummaryPayload { public string name; public string unit; public double p50; public double p95; public double p99; public double max; public double average; public int sampleCount; }
    [Serializable] public class ProfilerPeakFramePayload { public string metric; public double value; public int frame; public double elapsedSec; public string telemetryJson; }
    [Serializable] public class ProfilerComparisonItemPayload { public string name; public string unit; public double baselineP95; public double currentP95; public double p95Delta; public double p95DeltaPercent; public double baselineMax; public double currentMax; public double maxDelta; }
    [Serializable] public class ProfilerComparisonPayload { public string baselinePath; public string baselineCaptureId; public string currentCaptureId; public List<ProfilerComparisonItemPayload> metrics = new(); }
    [Serializable] public class ProfilerArtifactsPayload { public string jsonPath; public string csvPath; public string comparisonPath; }
    [Serializable] public class ProfilerCaptureResultPayload
    {
        public bool ok = true;
        public string captureId;
        public string status;
        public string title;
        public long startedAt;
        public long endedAt;
        public float durationSec;
        public double elapsedSec;
        public string elapsedSource;
        public bool elapsedFrozen;
        public int sampleEveryFrames;
        public int sampleCount;
        public int droppedSamples;
        public string jsonPath;
        public string csvPath;
        public string comparisonPath;
        public string baselineJsonPath;
        public string telemetrySampler;
        public int telemetryErrorCount;
        public string lastTelemetryError;
        public List<string> unavailableCounters = new();
        public List<string> selectedMarkers = new();
        public List<string> requestedMarkerPatterns = new();
        public string markerDiscoverySource;
        public List<ProfilerMetricSummaryPayload> summaries = new();
        public List<ProfilerMarkerSummaryPayload> topMarkers = new();
        public List<ProfilerPeakFramePayload> peakFrames = new();
        public ProfilerComparisonPayload comparison;
        public ProfilerArtifactsPayload artifacts = new();
        public List<string> limitations = new();
        public List<ProfilerCaptureSamplePayload> samples = new();
    }

    public sealed class UPilotRuntimeDiagnosticsService
    {
        private readonly UPilotBridge _bridge;
        private static long _lastNavMeshPreUpdateAt;
        private static long _navMeshPreUpdateVersion;
        private static long _navMeshObservationVersion;
        private static string _lastNavMeshSignature = "";
        private static readonly Dictionary<ulong, NavMeshTransformObservation> NavMeshTransformObservations = new();
        private static ProfilerCaptureState _profiler;

        private sealed class NavMeshTransformObservation
        {
            public string signature;
            public long version;
            public long changedAt;
            public long preUpdateVersionAtChange;
        }

        private sealed class ProfilerCaptureState
        {
            public ProfilerCaptureResultPayload result;
            public double startedEditorTime;
            public int lastSampledFrame = -1;
            public readonly Dictionary<string, ProfilerRecorder> recorders = new();
            public readonly Dictionary<string, string> markerUnits = new();
            public MethodInfo telemetryMethod;
        }

        static UPilotRuntimeDiagnosticsService()
        {
            NavMesh.onPreUpdate -= OnNavMeshPreUpdate;
            NavMesh.onPreUpdate += OnNavMeshPreUpdate;
        }

        public UPilotRuntimeDiagnosticsService(UPilotBridge bridge) { _bridge = bridge; }

        public void RegisterCommands()
        {
            _bridge.Router.Register("navmesh.status", HandleNavMeshStatusAsync);
            _bridge.Router.Register("navmesh.sample", HandleNavMeshSampleAsync);
            _bridge.Router.Register("navmesh.triangulationSummary", HandleNavMeshTriangulationAsync);
            _bridge.Router.Register("profiler.capture.start", HandleProfilerStartAsync);
            _bridge.Router.Register("profiler.capture.status", HandleProfilerStatusAsync);
            _bridge.Router.Register("profiler.capture.stop", HandleProfilerStopAsync);
        }

        private static void OnNavMeshPreUpdate()
        {
            _lastNavMeshPreUpdateAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _navMeshPreUpdateVersion++;
        }

        private async Task HandleNavMeshStatusAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<NavMeshStatusMessage>(json);
            await RunOnMainThread(id, "navmesh.status", token, () => BuildNavMeshStatus(message?.payload ?? new NavMeshStatusPayload()));
        }

        private async Task HandleNavMeshTriangulationAsync(string id, string json, CancellationToken token)
        {
            await RunOnMainThread(id, "navmesh.triangulationSummary", token, () => BuildTriangulation());
        }

        private async Task HandleNavMeshSampleAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<NavMeshSampleMessage>(json);
            await RunOnMainThread(id, "navmesh.sample", token, () => SampleNavMesh(message?.payload ?? new NavMeshSamplePayload()));
        }

        private async Task HandleProfilerStartAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<ProfilerCaptureStartMessage>(json);
            await RunOnMainThread(id, "profiler.capture.start", token, () => StartProfiler(message?.payload ?? new ProfilerCaptureStartPayload()));
        }

        private async Task HandleProfilerStatusAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<ProfilerCaptureStatusMessage>(json);
            await RunOnMainThread(id, "profiler.capture.status", token, () => GetProfilerStatus(message?.payload?.captureId));
        }

        private async Task HandleProfilerStopAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<ProfilerCaptureStatusMessage>(json);
            await RunOnMainThread(id, "profiler.capture.stop", token, () => StopProfiler(message?.payload?.captureId, "Stopped"));
        }

        private async Task RunOnMainThread<T>(string id, string command, CancellationToken token, Func<T> action)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try { tcs.TrySetResult(action()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            try { await _bridge.SendResultAsync(id, command, await tcs.Task, token); }
            catch (Exception ex) { await _bridge.SendErrorAsync(id, "RUNTIME_DIAGNOSTICS_FAILED", ex.Message, token, command); }
        }

        private static NavMeshStatusResultPayload BuildNavMeshStatus(NavMeshStatusPayload options)
        {
            var result = new NavMeshStatusResultPayload
            {
                observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                lastPreUpdateAt = _lastNavMeshPreUpdateAt,
                preUpdateVersion = _navMeshPreUpdateVersion,
            };
            if (options.includeSurfaces) result.surfaces = FindNavMeshSurfaces();
            if (options.includeAgents) result.agents = SummarizeAgents();
            if (options.includeTriangulation) result.triangulation = BuildTriangulation();
            result.observationSignature = BuildNavMeshSignature(result);
            if (!string.Equals(result.observationSignature, _lastNavMeshSignature, StringComparison.Ordinal))
            {
                _lastNavMeshSignature = result.observationSignature;
                _navMeshObservationVersion++;
            }
            result.observationVersion = _navMeshObservationVersion;
            return result;
        }

        private static List<NavMeshSurfaceInfoPayload> FindNavMeshSurfaces()
        {
            var result = new List<NavMeshSurfaceInfoPayload>();
            var surfaceType = FindType("Unity.AI.Navigation.NavMeshSurface") ?? FindType("UnityEngine.AI.NavMeshSurface");
            if (surfaceType == null) return result;
            foreach (var raw in Resources.FindObjectsOfTypeAll(surfaceType))
            {
                if (!(raw is Component component)) continue;
                var data = GetMemberValue(component, "navMeshData") as NavMeshData;
                var localBounds = data != null ? data.sourceBounds : new Bounds();
                var info = new NavMeshSurfaceInfoPayload
                {
                    gameObjectPath = GetHierarchyPath(component.transform),
                    instanceId = UPilotEntityIds.ToWireId(component),
                    enabled = component is Behaviour behaviour && behaviour.enabled,
                    activeInHierarchy = component.gameObject.activeInHierarchy,
                    componentType = surfaceType.FullName,
                    navMeshDataName = data != null ? data.name : "",
                    agentTypeId = ConvertToInt(GetMemberValue(component, "agentTypeID"), -1),
                    sourceLayerMask = ConvertToInt(GetMemberValue(component, "layerMask"), -1),
                    defaultArea = ConvertToInt(GetMemberValue(component, "defaultArea"), 0),
                    transformPosition = new NavMeshVectorPayload(component.transform.position),
                    transformEulerAngles = new NavMeshVectorPayload(component.transform.eulerAngles),
                    transformLossyScale = new NavMeshVectorPayload(component.transform.lossyScale),
                    sourceBounds = ToBoundsPayload(localBounds),
                    inferredWorldBounds = ToBoundsPayload(TransformBounds(component.transform.localToWorldMatrix, localBounds)),
                    registrationState = "not_registered",
                };
                var instance = GetMemberValue(component, "m_NavMeshDataInstance");
                if (instance != null)
                {
                    info.navMeshDataInstanceId = ConvertToInt(GetMemberValue(instance, "id"), 0);
                    info.navMeshDataInstanceValid = ConvertToBool(GetMemberValue(instance, "valid"));
                    info.registrationState = info.navMeshDataInstanceValid ? "registered_inferred" : "instance_invalid";
                }
                ApplyNavMeshTransformObservation(info, component.transform, data);
                result.Add(info);
            }
            return result.OrderBy(item => item.gameObjectPath, StringComparer.Ordinal).ToList();
        }

        private static void ApplyNavMeshTransformObservation(
            NavMeshSurfaceInfoPayload info,
            Transform transform,
            NavMeshData data)
        {
            string signature = string.Join("|",
                transform.position.ToString("R"),
                transform.rotation.eulerAngles.ToString("R"),
                transform.lossyScale.ToString("R"),
                data != null ? UPilotEntityIds.ToWireId(data).ToString(CultureInfo.InvariantCulture) : "0");
            if (!NavMeshTransformObservations.TryGetValue(info.instanceId, out var observation))
            {
                observation = new NavMeshTransformObservation
                {
                    signature = signature,
                    version = 1,
                    changedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    preUpdateVersionAtChange = _navMeshPreUpdateVersion,
                };
                NavMeshTransformObservations[info.instanceId] = observation;
            }
            else if (!string.Equals(observation.signature, signature, StringComparison.Ordinal))
            {
                observation.signature = signature;
                observation.version++;
                observation.changedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                observation.preUpdateVersionAtChange = _navMeshPreUpdateVersion;
            }
            info.surfaceTransformVersion = observation.version;
            info.transformChangedAt = observation.changedAt;
            info.transformObservedPreUpdateVersion = _navMeshPreUpdateVersion;
            info.transformAwaitingPreUpdate = _navMeshPreUpdateVersion <= observation.preUpdateVersionAtChange;
        }

        private static NavMeshAgentSummaryPayload SummarizeAgents()
        {
            var agents = Resources.FindObjectsOfTypeAll<NavMeshAgent>();
            var result = new NavMeshAgentSummaryPayload { total = agents.Length };
            foreach (var agent in agents)
            {
                if (agent.enabled) result.enabled++;
                if (agent.gameObject.activeInHierarchy) result.active++;
                if (agent.enabled && agent.gameObject.activeInHierarchy && agent.isOnNavMesh) result.onNavMesh++;
                else result.offNavMesh++;
            }
            return result;
        }

        private static NavMeshTriangulationPayload BuildTriangulation()
        {
            var triangulation = NavMesh.CalculateTriangulation();
            var bounds = new Bounds();
            if (triangulation.vertices != null && triangulation.vertices.Length > 0)
            {
                bounds = new Bounds(triangulation.vertices[0], Vector3.zero);
                for (var i = 1; i < triangulation.vertices.Length; i++) bounds.Encapsulate(triangulation.vertices[i]);
            }
            return new NavMeshTriangulationPayload
            {
                vertexCount = triangulation.vertices?.Length ?? 0,
                triangleCount = (triangulation.indices?.Length ?? 0) / 3,
                areaCount = triangulation.areas?.Distinct().Count() ?? 0,
                worldBounds = ToBoundsPayload(bounds),
            };
        }

        private static NavMeshSampleResultPayload SampleNavMesh(NavMeshSamplePayload payload)
        {
            var result = new NavMeshSampleResultPayload
            {
                maxDistance = Mathf.Max(0.001f, payload.maxDistance),
                areaMask = payload.areaMask,
                agentTypeId = payload.agentTypeId,
            };
            var surfaces = FindNavMeshSurfaces();
            var filter = new NavMeshQueryFilter { areaMask = payload.areaMask, agentTypeID = payload.agentTypeId };
            foreach (var point in payload.points ?? new List<NavMeshPointPayload>())
            {
                var requested = new Vector3(point.x, point.y, point.z);
                NavMeshHit hit;
                bool found = payload.agentTypeId >= 0
                    ? NavMesh.SamplePosition(requested, out hit, result.maxDistance, filter)
                    : NavMesh.SamplePosition(requested, out hit, result.maxDistance, payload.areaMask);
                var item = new NavMeshSampleItemPayload
                {
                    requested = new NavMeshVectorPayload(requested),
                    found = found,
                    hitPosition = new NavMeshVectorPayload(found ? hit.position : requested),
                    distance = found ? Vector3.Distance(requested, hit.position) : -1f,
                    mask = found ? hit.mask : 0,
                };
                if (found)
                {
                    NavMeshHit edge;
                    item.edgeFound = payload.agentTypeId >= 0
                        ? NavMesh.FindClosestEdge(hit.position, out edge, filter)
                        : NavMesh.FindClosestEdge(hit.position, out edge, payload.areaMask);
                    if (item.edgeFound)
                    {
                        item.edgePosition = new NavMeshVectorPayload(edge.position);
                        item.edgeDistance = Vector3.Distance(hit.position, edge.position);
                    }
                    var matching = surfaces.FirstOrDefault(surface => Contains(surface.inferredWorldBounds, hit.position));
                    if (matching != null)
                    {
                        item.matchingSurfacePath = matching.gameObjectPath;
                        item.matchingConfidence = "inferred-world-bounds";
                        item.matchingNavMeshDataInstanceId = matching.navMeshDataInstanceId;
                        item.matchingAgentTypeId = matching.agentTypeId;
                        item.registrationMatrixSource = matching.registrationMatrixSource;
                    }
                }
                result.samples.Add(item);
            }
            return result;
        }

        private static ProfilerCaptureResultPayload StartProfiler(ProfilerCaptureStartPayload payload)
        {
            if (_profiler != null && _profiler.result.status == "Running")
                throw new InvalidOperationException("A profiler capture is already running: " + _profiler.result.captureId);
            var duration = Mathf.Clamp(payload.durationSec, 1f, 3600f);
            var state = new ProfilerCaptureState
            {
                startedEditorTime = EditorApplication.timeSinceStartup,
                result = new ProfilerCaptureResultPayload
                {
                    captureId = "profiler_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture),
                    status = "Running",
                    title = string.IsNullOrWhiteSpace(payload.title) ? "runtime-profiler" : payload.title.Trim(),
                    startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    durationSec = duration,
                    elapsedSource = "EditorApplication.timeSinceStartup",
                    elapsedFrozen = false,
                    sampleEveryFrames = Mathf.Clamp(payload.sampleEveryFrames, 1, 600),
                    jsonPath = ResolveProfilerPath(payload.outputDirectory, payload.title, ".json"),
                    csvPath = ResolveProfilerPath(payload.outputDirectory, payload.title, ".csv"),
                    comparisonPath = ResolveProfilerPath(payload.outputDirectory, payload.title + "_comparison", ".json"),
                    baselineJsonPath = payload.baselineJsonPath ?? "",
                }
            };
            AddRecorder(state, "mainThreadMs", ProfilerCategory.Internal, "Main Thread", state.result.unavailableCounters);
            AddRecorder(state, "renderThreadMs", ProfilerCategory.Internal, "Render Thread", state.result.unavailableCounters);
            AddRecorder(state, "gpuFrameMs", ProfilerCategory.Render, "GPU Frame Time", state.result.unavailableCounters);
            AddRecorder(state, "gcAllocatedBytes", ProfilerCategory.Memory, "GC Allocated In Frame", state.result.unavailableCounters);
            AddRecorder(state, "gcCollectionCount", ProfilerCategory.Memory, "GC Collection Count", state.result.unavailableCounters);
            AddRecorder(state, "managedHeapUsedBytes", ProfilerCategory.Memory, "GC Used Memory", state.result.unavailableCounters);
            AddRecorder(state, "managedHeapReservedBytes", ProfilerCategory.Memory, "GC Reserved Memory", state.result.unavailableCounters);
            AddRecorder(state, "drawCalls", ProfilerCategory.Render, "Draw Calls Count", state.result.unavailableCounters);
            AddRecorder(state, "setPassCalls", ProfilerCategory.Render, "SetPass Calls Count", state.result.unavailableCounters);
            AddRecorder(state, "batches", ProfilerCategory.Render, "Batches Count", state.result.unavailableCounters);
            AddRecorder(state, "triangles", ProfilerCategory.Render, "Triangles Count", state.result.unavailableCounters);
            AddRecorder(state, "vertices", ProfilerCategory.Render, "Vertices Count", state.result.unavailableCounters);
            AddRequestedMarkerRecorders(state, payload);
            ConfigureTelemetrySampler(state, payload);
            state.result.artifacts = new ProfilerArtifactsPayload
            {
                jsonPath = state.result.jsonPath,
                csvPath = state.result.csvPath,
                comparisonPath = string.IsNullOrWhiteSpace(state.result.baselineJsonPath) ? "" : state.result.comparisonPath,
            };
            state.result.limitations.Add("ProfilerRecorder provides aggregate marker/counter values, not full per-thread Timeline event trees.");
            state.result.limitations.Add("URP pass timing is available only when matching ProfilerRecorder markers are exposed by the active Unity/URP version.");
            _profiler = state;
            EditorApplication.update -= SampleProfiler;
            EditorApplication.update += SampleProfiler;
            return CloneProfilerResult(state.result, false);
        }

        private static void AddRecorder(ProfilerCaptureState state, string key, ProfilerCategory category, string marker, List<string> unavailable)
        {
            try
            {
                var recorder = ProfilerRecorder.StartNew(category, marker, 1);
                if (!recorder.Valid) { recorder.Dispose(); unavailable.Add(marker); return; }
                state.recorders[key] = recorder;
            }
            catch { unavailable.Add(marker); }
        }

        private static void AddRequestedMarkerRecorders(ProfilerCaptureState state, ProfilerCaptureStartPayload payload)
        {
            var available = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(available);
            var descriptions = available
                .Where(handle => handle.Valid)
                .Select(handle => new { handle, description = ProfilerRecorderHandle.GetDescription(handle) })
                .Where(item => !string.IsNullOrWhiteSpace(item.description.Name))
                .ToList();
            var requested = new HashSet<string>(payload.markerNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            state.result.markerDiscoverySource = "ProfilerRecorderHandle.GetAvailable";
            if (payload.includeDefaultAiMarkers)
            {
                state.result.requestedMarkerPatterns.Add("NavMesh|Nav Mesh|AI|Animator");
                foreach (var name in descriptions
                    .Select(item => item.description.Name)
                    .Where(name => name.IndexOf("NavMesh", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("Nav Mesh", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.StartsWith("AI", StringComparison.OrdinalIgnoreCase)
                        || name.IndexOf("Animator", StringComparison.OrdinalIgnoreCase) >= 0))
                    requested.Add(name);
            }
            foreach (string name in payload.markerNames ?? Array.Empty<string>())
                state.result.requestedMarkerPatterns.Add(name);
            System.Text.RegularExpressions.Regex regex = null;
            if (!string.IsNullOrWhiteSpace(payload.markerNameRegex))
                regex = new System.Text.RegularExpressions.Regex(payload.markerNameRegex, System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            int maxMarkers = Mathf.Clamp(payload.maxMarkers, 0, 256);
            foreach (var item in descriptions
                .Where(item => requested.Contains(item.description.Name) || (regex != null && regex.IsMatch(item.description.Name)))
                .GroupBy(item => item.description.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(maxMarkers))
            {
                try
                {
                    var recorder = new ProfilerRecorder(item.handle, 1, ProfilerRecorderOptions.StartImmediately | ProfilerRecorderOptions.SumAllSamplesInFrame);
                    if (!recorder.Valid) { recorder.Dispose(); state.result.unavailableCounters.Add(item.description.Name); continue; }
                    string key = "marker:" + item.description.Name;
                    state.recorders[key] = recorder;
                    state.markerUnits[key] = item.description.UnitType.ToString();
                    state.result.selectedMarkers.Add(item.description.Name);
                }
                catch { state.result.unavailableCounters.Add(item.description.Name); }
            }
            foreach (string name in requested.Where(name => !state.result.selectedMarkers.Contains(name, StringComparer.OrdinalIgnoreCase)))
                state.result.unavailableCounters.Add(name);
        }

        private static void ConfigureTelemetrySampler(ProfilerCaptureState state, ProfilerCaptureStartPayload payload)
        {
            if (string.IsNullOrWhiteSpace(payload.telemetryTypeName) || string.IsNullOrWhiteSpace(payload.telemetryMethodName)) return;
            var type = FindType(payload.telemetryTypeName);
            state.telemetryMethod = type?.GetMethod(payload.telemetryMethodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (state.telemetryMethod == null)
            {
                state.result.telemetryErrorCount++;
                state.result.lastTelemetryError = "Telemetry sampler must be a compiled static parameterless method: " + payload.telemetryTypeName + "." + payload.telemetryMethodName;
                return;
            }
            state.result.telemetrySampler = payload.telemetryTypeName + "." + payload.telemetryMethodName;
        }

        private static void SampleProfiler()
        {
            var state = _profiler;
            if (state == null || state.result.status != "Running") return;
            var elapsed = EditorApplication.timeSinceStartup - state.startedEditorTime;
            if (elapsed >= state.result.durationSec) { StopProfiler(state.result.captureId, "Completed"); return; }
            if (Time.frameCount == state.lastSampledFrame || Time.frameCount % state.result.sampleEveryFrames != 0) return;
            state.lastSampledFrame = Time.frameCount;
            var sample = new ProfilerCaptureSamplePayload
            {
                frame = Time.frameCount,
                elapsedSec = elapsed,
                mainThreadMs = ReadTimeMs(state, "mainThreadMs"),
                renderThreadMs = ReadTimeMs(state, "renderThreadMs"),
                gpuFrameMs = ReadTimeMs(state, "gpuFrameMs"),
                gcAllocatedBytes = ReadValue(state, "gcAllocatedBytes"),
                gcCollectionCount = ReadValue(state, "gcCollectionCount"),
                managedHeapUsedBytes = ReadValue(state, "managedHeapUsedBytes"),
                managedHeapReservedBytes = ReadValue(state, "managedHeapReservedBytes"),
                drawCalls = ReadValue(state, "drawCalls"),
                setPassCalls = ReadValue(state, "setPassCalls"),
                batches = ReadValue(state, "batches"),
                triangles = ReadValue(state, "triangles"),
                vertices = ReadValue(state, "vertices"),
                navMeshAgents = Resources.FindObjectsOfTypeAll<NavMeshAgent>().Length,
                animators = Resources.FindObjectsOfTypeAll<Animator>().Length,
                skinnedMeshRenderers = Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>().Length,
                particleSystems = Resources.FindObjectsOfTypeAll<ParticleSystem>().Length,
            };
            foreach (var pair in state.recorders.Where(pair => pair.Key.StartsWith("marker:", StringComparison.Ordinal)))
            {
                string unit = state.markerUnits.TryGetValue(pair.Key, out var value) ? value : "Count";
                double raw = pair.Value.Valid ? pair.Value.LastValueAsDouble : 0d;
                sample.markers.Add(new ProfilerMarkerValuePayload
                {
                    name = pair.Key.Substring("marker:".Length),
                    unit = unit,
                    value = IsTimeUnit(unit) ? raw / 1000000.0 : raw,
                });
            }
            if (state.telemetryMethod != null)
            {
                try
                {
                    object telemetry = state.telemetryMethod.Invoke(null, null);
                    sample.telemetryJson = telemetry is string text ? text : JsonUtility.ToJson(telemetry);
                }
                catch (Exception ex)
                {
                    state.result.telemetryErrorCount++;
                    state.result.lastTelemetryError = ex.InnerException?.Message ?? ex.Message;
                }
            }
            state.result.samples.Add(sample);
            state.result.sampleCount = state.result.samples.Count;
            state.result.elapsedSec = elapsed;
        }

        private static ProfilerCaptureResultPayload GetProfilerStatus(string captureId)
        {
            if (_profiler == null) throw new InvalidOperationException("No profiler capture exists in this domain.");
            if (!string.IsNullOrWhiteSpace(captureId) && !string.Equals(captureId, _profiler.result.captureId, StringComparison.Ordinal))
                throw new InvalidOperationException("Profiler capture not found: " + captureId);
            if (_profiler.result.status == "Running")
                _profiler.result.elapsedSec = EditorApplication.timeSinceStartup - _profiler.startedEditorTime;
            return CloneProfilerResult(_profiler.result, false);
        }

        private static ProfilerCaptureResultPayload StopProfiler(string captureId, string terminalStatus)
        {
            if (_profiler == null) throw new InvalidOperationException("No profiler capture exists in this domain.");
            if (!string.IsNullOrWhiteSpace(captureId) && !string.Equals(captureId, _profiler.result.captureId, StringComparison.Ordinal))
                throw new InvalidOperationException("Profiler capture not found: " + captureId);
            EditorApplication.update -= SampleProfiler;
            var state = _profiler;
            state.result.status = terminalStatus;
            state.result.endedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            state.result.elapsedSec = EditorApplication.timeSinceStartup - state.startedEditorTime;
            state.result.elapsedFrozen = true;
            foreach (var recorder in state.recorders.Values) recorder.Dispose();
            state.recorders.Clear();
            state.result.summaries = BuildProfilerSummaries(state.result.samples);
            state.result.topMarkers = BuildMarkerSummaries(state.result.samples).Take(20).ToList();
            state.result.peakFrames = BuildPeakFrames(state.result.samples);
            state.result.comparison = BuildProfilerComparison(state.result);
            WriteProfilerArtifacts(state.result);
            return CloneProfilerResult(state.result, true);
        }

        private static long ReadValue(ProfilerCaptureState state, string key) => state.recorders.TryGetValue(key, out var recorder) && recorder.Valid ? recorder.LastValue : 0;
        private static double ReadTimeMs(ProfilerCaptureState state, string key) => ReadValue(state, key) / 1000000.0;

        private static List<ProfilerMetricSummaryPayload> BuildProfilerSummaries(List<ProfilerCaptureSamplePayload> samples)
        {
            var result = new List<ProfilerMetricSummaryPayload>();
            AddSummary(result, "mainThreadMs", "ms", samples.Select(v => v.mainThreadMs));
            AddSummary(result, "renderThreadMs", "ms", samples.Select(v => v.renderThreadMs));
            AddSummary(result, "gpuFrameMs", "ms", samples.Select(v => v.gpuFrameMs));
            AddSummary(result, "gcAllocatedBytes", "bytes", samples.Select(v => (double)v.gcAllocatedBytes));
            AddSummary(result, "gcCollectionCount", "count", samples.Select(v => (double)v.gcCollectionCount));
            AddSummary(result, "managedHeapUsedBytes", "bytes", samples.Select(v => (double)v.managedHeapUsedBytes));
            AddSummary(result, "managedHeapReservedBytes", "bytes", samples.Select(v => (double)v.managedHeapReservedBytes));
            AddSummary(result, "drawCalls", "count", samples.Select(v => (double)v.drawCalls));
            AddSummary(result, "setPassCalls", "count", samples.Select(v => (double)v.setPassCalls));
            AddSummary(result, "batches", "count", samples.Select(v => (double)v.batches));
            AddSummary(result, "triangles", "count", samples.Select(v => (double)v.triangles));
            AddSummary(result, "vertices", "count", samples.Select(v => (double)v.vertices));
            AddSummary(result, "navMeshAgents", "count", samples.Select(v => (double)v.navMeshAgents));
            AddSummary(result, "animators", "count", samples.Select(v => (double)v.animators));
            AddSummary(result, "skinnedMeshRenderers", "count", samples.Select(v => (double)v.skinnedMeshRenderers));
            AddSummary(result, "particleSystems", "count", samples.Select(v => (double)v.particleSystems));
            return result;
        }

        private static IEnumerable<ProfilerMarkerSummaryPayload> BuildMarkerSummaries(List<ProfilerCaptureSamplePayload> samples)
        {
            return samples
                .SelectMany(sample => sample.markers ?? new List<ProfilerMarkerValuePayload>())
                .GroupBy(marker => marker.name, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var ordered = group.Select(marker => marker.value).OrderBy(value => value).ToArray();
                    return new ProfilerMarkerSummaryPayload
                    {
                        name = group.Key,
                        unit = group.Select(marker => marker.unit).FirstOrDefault() ?? "",
                        p50 = Percentile(ordered, .50),
                        p95 = Percentile(ordered, .95),
                        p99 = Percentile(ordered, .99),
                        max = ordered.Length == 0 ? 0 : ordered[ordered.Length - 1],
                        average = ordered.Length == 0 ? 0 : ordered.Average(),
                        sampleCount = ordered.Length,
                    };
                })
                .OrderByDescending(item => item.p95);
        }

        private static List<ProfilerPeakFramePayload> BuildPeakFrames(List<ProfilerCaptureSamplePayload> samples)
        {
            var result = new List<ProfilerPeakFramePayload>();
            AddPeak(result, samples, "mainThreadMs", sample => sample.mainThreadMs);
            AddPeak(result, samples, "renderThreadMs", sample => sample.renderThreadMs);
            AddPeak(result, samples, "gpuFrameMs", sample => sample.gpuFrameMs);
            AddPeak(result, samples, "gcAllocatedBytes", sample => sample.gcAllocatedBytes);
            AddPeak(result, samples, "drawCalls", sample => sample.drawCalls);
            return result;
        }

        private static void AddPeak(List<ProfilerPeakFramePayload> target, List<ProfilerCaptureSamplePayload> samples, string metric, Func<ProfilerCaptureSamplePayload, double> selector)
        {
            if (samples == null || samples.Count == 0) return;
            var sample = samples.OrderByDescending(selector).First();
            target.Add(new ProfilerPeakFramePayload { metric = metric, value = selector(sample), frame = sample.frame, elapsedSec = sample.elapsedSec, telemetryJson = sample.telemetryJson });
        }

        private static ProfilerComparisonPayload BuildProfilerComparison(ProfilerCaptureResultPayload current)
        {
            if (string.IsNullOrWhiteSpace(current.baselineJsonPath)) return null;
            string path = current.baselineJsonPath;
            if (!Path.IsPathRooted(path)) path = Path.Combine(Path.GetFullPath(Directory.GetCurrentDirectory()), path);
            path = Path.GetFullPath(path);
            if (!File.Exists(path))
            {
                current.lastTelemetryError = "Baseline profiler JSON not found: " + path;
                return null;
            }
            try
            {
                var baseline = JsonUtility.FromJson<ProfilerCaptureResultPayload>(File.ReadAllText(path));
                var comparison = new ProfilerComparisonPayload { baselinePath = path, baselineCaptureId = baseline?.captureId ?? "", currentCaptureId = current.captureId };
                foreach (var summary in current.summaries)
                {
                    var before = baseline?.summaries?.FirstOrDefault(item => string.Equals(item.name, summary.name, StringComparison.OrdinalIgnoreCase));
                    if (before == null) continue;
                    comparison.metrics.Add(new ProfilerComparisonItemPayload
                    {
                        name = summary.name,
                        unit = summary.unit,
                        baselineP95 = before.p95,
                        currentP95 = summary.p95,
                        p95Delta = summary.p95 - before.p95,
                        p95DeltaPercent = Math.Abs(before.p95) < .000001 ? 0 : (summary.p95 - before.p95) / before.p95 * 100d,
                        baselineMax = before.max,
                        currentMax = summary.max,
                        maxDelta = summary.max - before.max,
                    });
                }
                return comparison;
            }
            catch (Exception ex)
            {
                current.lastTelemetryError = "Baseline comparison failed: " + ex.Message;
                return null;
            }
        }

        private static void AddSummary(List<ProfilerMetricSummaryPayload> target, string name, string unit, IEnumerable<double> values)
        {
            var ordered = values.OrderBy(v => v).ToArray();
            if (ordered.Length == 0) return;
            target.Add(new ProfilerMetricSummaryPayload { name = name, unit = unit, p50 = Percentile(ordered, .50), p95 = Percentile(ordered, .95), p99 = Percentile(ordered, .99), max = ordered[ordered.Length - 1], average = ordered.Average() });
        }

        private static double Percentile(double[] ordered, double percentile)
        {
            if (ordered.Length == 1) return ordered[0];
            var position = (ordered.Length - 1) * percentile;
            var lower = (int)Math.Floor(position);
            var upper = (int)Math.Ceiling(position);
            if (lower == upper) return ordered[lower];
            return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower);
        }

        private static void WriteProfilerArtifacts(ProfilerCaptureResultPayload result)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(result.jsonPath) ?? "Log/UPilotProfiler");
            File.WriteAllText(result.jsonPath, JsonUtility.ToJson(result, true));
            if (result.comparison != null)
                File.WriteAllText(result.comparisonPath, JsonUtility.ToJson(result.comparison, true));
            using (var writer = new StreamWriter(result.csvPath, false))
            {
                writer.WriteLine("frame,elapsedSec,mainThreadMs,renderThreadMs,gpuFrameMs,gcAllocatedBytes,gcCollectionCount,managedHeapUsedBytes,managedHeapReservedBytes,drawCalls,setPassCalls,batches,triangles,vertices,navMeshAgents,animators,skinnedMeshRenderers,particleSystems,telemetryJson");
                foreach (var s in result.samples)
                    writer.WriteLine(string.Join(",", s.frame, F(s.elapsedSec), F(s.mainThreadMs), F(s.renderThreadMs), F(s.gpuFrameMs), s.gcAllocatedBytes, s.gcCollectionCount, s.managedHeapUsedBytes, s.managedHeapReservedBytes, s.drawCalls, s.setPassCalls, s.batches, s.triangles, s.vertices, s.navMeshAgents, s.animators, s.skinnedMeshRenderers, s.particleSystems, Csv(s.telemetryJson)));
            }
        }

        private static string ResolveProfilerPath(string directory, string title, string extension)
        {
            var projectRoot = Path.GetFullPath(Directory.GetCurrentDirectory());
            var root = string.IsNullOrWhiteSpace(directory) ? Path.Combine(projectRoot, "Log", "UPilotProfiler") : directory;
            if (!Path.IsPathRooted(root)) root = Path.Combine(projectRoot, root);
            root = Path.GetFullPath(root);
            var projectPrefix = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!string.Equals(root, projectRoot, StringComparison.OrdinalIgnoreCase)
                && !root.StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Profiler outputDirectory must stay under the Unity project.");
            var safeTitle = string.Concat((string.IsNullOrWhiteSpace(title) ? "runtime-profiler" : title).Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            return Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + "_" + safeTitle + extension);
        }

        private static ProfilerCaptureResultPayload CloneProfilerResult(ProfilerCaptureResultPayload value, bool includeSamples)
        {
            var json = JsonUtility.ToJson(value);
            var clone = JsonUtility.FromJson<ProfilerCaptureResultPayload>(json);
            if (!includeSamples) clone.samples = new List<ProfilerCaptureSamplePayload>();
            return clone;
        }

        private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        private static string Csv(string value) => string.IsNullOrEmpty(value) ? "" : "\"" + value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        private static bool IsTimeUnit(string unit) => unit.IndexOf("Nanosecond", StringComparison.OrdinalIgnoreCase) >= 0 || unit.IndexOf("Time", StringComparison.OrdinalIgnoreCase) >= 0;
        private static Type FindType(string fullName) => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(fullName, false)).FirstOrDefault(type => type != null);
        private static object GetMemberValue(object instance, string name)
        {
            if (instance == null) return null;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = instance.GetType();
            var property = type.GetProperty(name, flags);
            if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(instance, null);
            return type.GetField(name, flags)?.GetValue(instance);
        }
        private static int ConvertToInt(object value, int fallback) { try { return value == null ? fallback : Convert.ToInt32(value, CultureInfo.InvariantCulture); } catch { return fallback; } }
        private static bool ConvertToBool(object value) { try { return value != null && Convert.ToBoolean(value, CultureInfo.InvariantCulture); } catch { return false; } }
        private static NavMeshBoundsPayload ToBoundsPayload(Bounds bounds) => new NavMeshBoundsPayload { center = new NavMeshVectorPayload(bounds.center), size = new NavMeshVectorPayload(bounds.size) };
        private static Bounds TransformBounds(Matrix4x4 matrix, Bounds local)
        {
            var center = matrix.MultiplyPoint3x4(local.center);
            var extents = local.extents;
            var axisX = matrix.MultiplyVector(new Vector3(extents.x, 0, 0));
            var axisY = matrix.MultiplyVector(new Vector3(0, extents.y, 0));
            var axisZ = matrix.MultiplyVector(new Vector3(0, 0, extents.z));
            var worldExtents = new Vector3(Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x), Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y), Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
            return new Bounds(center, worldExtents * 2f);
        }
        private static bool Contains(NavMeshBoundsPayload bounds, Vector3 point)
        {
            if (bounds?.center == null || bounds.size == null) return false;
            return new Bounds(new Vector3(bounds.center.x, bounds.center.y, bounds.center.z), new Vector3(bounds.size.x, bounds.size.y, bounds.size.z)).Contains(point);
        }
        private static string GetHierarchyPath(Transform transform)
        {
            var parts = new Stack<string>();
            while (transform != null) { parts.Push(transform.name); transform = transform.parent; }
            return string.Join("/", parts);
        }
        private static string BuildNavMeshSignature(NavMeshStatusResultPayload result)
        {
            var surfaceSignature = string.Join("|", result.surfaces.Select(s => s.instanceId + ":" + s.navMeshDataInstanceId + ":" + s.navMeshDataInstanceValid + ":" + s.transformPosition.x + "," + s.transformPosition.y + "," + s.transformPosition.z));
            var triangulation = result.triangulation;
            return surfaceSignature + "#" + (triangulation?.vertexCount ?? 0) + ":" + (triangulation?.triangleCount ?? 0);
        }
    }
}
