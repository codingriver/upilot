using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    internal static class UPilotAllocationMeasurement
    {
        private const int UnavailableMode = 0;
        private const int ManagedThreadMode = 1;
        private const int MonoProfilerMode = 2;
        private static readonly object Gate = new object();
        private static int s_mode;
        private static bool s_initialized;

#if UNITY_EDITOR_WIN && ENABLE_MONO
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void MonoAllocationCallback(IntPtr profiler, IntPtr value);

        private static readonly MonoAllocationCallback AllocationCallback = OnMonoAllocation;
        private static IntPtr s_profiler;
        private static int s_activeMonoScopes;
        [ThreadStatic] private static bool t_measuring;
        [ThreadStatic] private static int t_measurementDepth;
        [ThreadStatic] private static long t_allocatedBytes;

        [DllImport("mono-2.0-bdwgc", CallingConvention = CallingConvention.Cdecl)]
        private static extern void mono_profiler_enable_allocations();

        [DllImport("mono-2.0-bdwgc", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mono_profiler_create(IntPtr state);

        [DllImport("mono-2.0-bdwgc", CallingConvention = CallingConvention.Cdecl)]
        private static extern void mono_profiler_set_gc_allocation_callback(
            IntPtr profiler, MonoAllocationCallback callback);

        [DllImport("mono-2.0-bdwgc", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint mono_object_get_size(IntPtr value);
#endif

        internal readonly struct Token
        {
            internal readonly int mode;
            internal readonly long baseline;
            internal Token(int mode, long baseline)
            {
                this.mode = mode;
                this.baseline = baseline;
            }
        }

        internal static bool Available
        {
            get
            {
                EnsureInitialized();
                return s_mode != UnavailableMode;
            }
        }

        internal static string Source
        {
            get
            {
                EnsureInitialized();
                return s_mode == ManagedThreadMode ? "gcThreadCounter" :
                    s_mode == MonoProfilerMode ? "monoAllocationCallback" : "unavailable";
            }
        }

        internal static Token Begin()
        {
            EnsureInitialized();
            if (s_mode == ManagedThreadMode)
                return new Token(ManagedThreadMode, GC.GetAllocatedBytesForCurrentThread());
#if UNITY_EDITOR_WIN && ENABLE_MONO
            if (s_mode == MonoProfilerMode)
            {
                if (t_measurementDepth == 0)
                {
                    lock (Gate)
                    {
                        if (s_activeMonoScopes++ == 0)
                            mono_profiler_set_gc_allocation_callback(s_profiler, AllocationCallback);
                    }
                    t_allocatedBytes = 0;
                    t_measuring = true;
                }
                t_measurementDepth++;
                return new Token(MonoProfilerMode, t_allocatedBytes);
            }
#endif
            return new Token(UnavailableMode, 0);
        }

        internal static long End(Token token)
        {
            if (token.mode == ManagedThreadMode)
                return Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - token.baseline);
#if UNITY_EDITOR_WIN && ENABLE_MONO
            if (token.mode == MonoProfilerMode && t_measurementDepth > 0)
            {
                long result = Math.Max(0, t_allocatedBytes - token.baseline);
                if (--t_measurementDepth == 0)
                {
                    t_measuring = false;
                    lock (Gate)
                    {
                        if (--s_activeMonoScopes == 0)
                            mono_profiler_set_gc_allocation_callback(s_profiler, null);
                    }
                }
                return result;
            }
#endif
            return -1;
        }

        private static void EnsureInitialized()
        {
            if (s_initialized) return;
            lock (Gate)
            {
                if (s_initialized) return;
                s_mode = ProbeManagedCounter() ? ManagedThreadMode :
                    ProbeMonoProfilerCounter() ? MonoProfilerMode : UnavailableMode;
                s_initialized = true;
            }
        }

        private static bool ProbeManagedCounter()
        {
            try
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                var probe = new byte[4096];
                long delta = GC.GetAllocatedBytesForCurrentThread() - before;
                GC.KeepAlive(probe);
                return delta >= 4096;
            }
            catch { return false; }
        }

        private static bool ProbeMonoProfilerCounter()
        {
#if UNITY_EDITOR_WIN && ENABLE_MONO
            try
            {
                mono_profiler_enable_allocations();
                s_profiler = mono_profiler_create(IntPtr.Zero);
                if (s_profiler == IntPtr.Zero) return false;
                mono_profiler_set_gc_allocation_callback(s_profiler, AllocationCallback);
                var warmup = new byte[32];
                GC.KeepAlive(warmup);
                t_allocatedBytes = 0;
                t_measuring = true;
                var probe = new byte[4096];
                t_measuring = false;
                GC.KeepAlive(probe);
                mono_profiler_set_gc_allocation_callback(s_profiler, null);
                return t_allocatedBytes >= 4096;
            }
            catch
            {
                try
                {
                    if (s_profiler != IntPtr.Zero)
                        mono_profiler_set_gc_allocation_callback(s_profiler, null);
                }
                catch { }
                return false;
            }
            finally { t_measuring = false; }
#else
            return false;
#endif
        }

#if UNITY_EDITOR_WIN && ENABLE_MONO
        private static void OnMonoAllocation(IntPtr profiler, IntPtr value)
        {
            if (t_measuring && value != IntPtr.Zero)
                t_allocatedBytes += mono_object_get_size(value);
        }
#endif
    }

    [Serializable]
    public sealed class WindowTelemetrySample
    {
        public string instanceId;
        public int threadId;
        public int frame;
        public string frameSource;
        public int onGuiCalls;
        public double onGuiMs;
        public long allocatedBytes;
        public bool allocationMeasurementAvailable;
        public string allocationMeasurementSource;
        public int repaintRequests;
    }

    /// <summary>Explicit instrumentation only. No automatic window interception.</summary>
    public static class UPilotWindowTelemetry
    {
        private const int Capacity = 4096;
        private static readonly object Gate = new object();
        private static Dictionary<(ulong, int, int), int> s_indices;
        private static Dictionary<EditorWindow, ulong> s_windowIds;
        private static WindowTelemetrySample[] s_samples;
        private static int s_count;
        private static int s_dropped;
        private static bool s_enabled;
        private static int s_generation;
        private static int s_editorOnGuiSequence;
        private static Dictionary<(ulong, int), int> s_lastEditorFrameByWindow;
        internal static bool AllocationMeasurementAvailable { get; private set; }

        internal static bool ProbeAllocationCounter()
        {
            return UPilotAllocationMeasurement.Available;
        }

        internal static void BeginCapture()
        {
            lock (Gate)
            {
                s_generation++;
                AllocationMeasurementAvailable = ProbeAllocationCounter();
                s_indices = new Dictionary<(ulong, int, int), int>(Capacity);
                s_windowIds = new Dictionary<EditorWindow, ulong>();
                s_lastEditorFrameByWindow = new Dictionary<(ulong, int), int>();
                s_samples = new WindowTelemetrySample[Capacity];
                for (int index = 0; index < Capacity; index++) s_samples[index] = new WindowTelemetrySample();
                s_count = s_dropped = 0;
                s_editorOnGuiSequence = 0;
                s_enabled = true;
            }
        }

        private static int GetIndex(EditorWindow window, bool beginOnGui)
        {
            if (!s_enabled || window == null) return -1;
            if (!s_windowIds.TryGetValue(window, out ulong instanceId))
            {
                instanceId = UPilotEntityIds.ToWireId(window);
                s_windowIds.Add(window, instanceId);
            }
            int threadId = Thread.CurrentThread.ManagedThreadId;
            int frame = Time.frameCount;
            string frameSource = "Time.frameCount";
            if (!EditorApplication.isPlaying)
            {
                frameSource = "editorOnGuiSequence";
                var windowKey = (instanceId, threadId);
                if (beginOnGui)
                {
                    frame = ++s_editorOnGuiSequence;
                    s_lastEditorFrameByWindow[windowKey] = frame;
                }
                else if (!s_lastEditorFrameByWindow.TryGetValue(windowKey, out frame))
                {
                    frame = 0;
                }
            }
            var key = (instanceId, threadId, frame);
            if (s_indices.TryGetValue(key, out int index)) return index;
            if (s_count == Capacity) { s_dropped++; return -1; }
            index = s_count++;
            s_indices.Add(key, index);
            var sample = s_samples[index];
            sample.instanceId = key.Item1.ToString();
            sample.threadId = key.Item2;
            sample.frame = key.Item3;
            sample.frameSource = frameSource;
            sample.allocationMeasurementAvailable = AllocationMeasurementAvailable;
            sample.allocationMeasurementSource = UPilotAllocationMeasurement.Source;
            sample.allocatedBytes = AllocationMeasurementAvailable ? 0 : -1;
            return index;
        }

        public static Scope OnGUI(EditorWindow window)
        {
            lock (Gate) return new Scope(GetIndex(window, true), s_generation);
        }

        public static void RequestRepaint(EditorWindow window)
        {
            lock (Gate)
            {
                int index = GetIndex(window, false);
                if (index >= 0) s_samples[index].repaintRequests++;
            }
            if (window != null) window.Repaint();
        }

        public struct Scope : IDisposable
        {
            private int _index;
            private readonly int _generation;
            private readonly long _started;
            private readonly UPilotAllocationMeasurement.Token _allocation;
            internal Scope(int index, int generation)
            {
                _index = index;
                _generation = generation;
                _started = index < 0 ? 0 : Stopwatch.GetTimestamp();
                _allocation = index < 0 || !AllocationMeasurementAvailable
                    ? default : UPilotAllocationMeasurement.Begin();
            }
            public void Dispose()
            {
                if (_index < 0) return;
                long allocated = UPilotAllocationMeasurement.End(_allocation);
                double elapsed = (Stopwatch.GetTimestamp() - _started) * 1000d / Stopwatch.Frequency;
                lock (Gate)
                    if (s_enabled && _generation == s_generation)
                    {
                        var sample = s_samples[_index];
                        sample.onGuiCalls++;
                        sample.onGuiMs += elapsed;
                        if (sample.allocationMeasurementAvailable && allocated >= 0) sample.allocatedBytes += allocated;
                    }
                _index = -1;
            }
        }

        internal static List<WindowTelemetrySample> EndCapture(out int dropped)
        {
            lock (Gate)
            {
                s_enabled = false;
                dropped = s_dropped;
                var result = new List<WindowTelemetrySample>(s_count);
                for (int index = 0; index < s_count; index++) result.Add(s_samples[index]);
                return result;
            }
        }
    }
}
