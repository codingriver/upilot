// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Process = System.Diagnostics.Process;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable]
    public sealed class EditorWindowPixelCapture
    {
        public string imageData;
        public int width;
        public int height;
        public string captureApi;
        public long windowHandle;
        public int unityProcessId;
        public int windowLeft;
        public int windowTop;
        public int windowRight;
        public int windowBottom;
        public bool foreground;
        public bool occlusionSensitive;
        public bool pixelSourceVerified;
        public bool degraded;
        public string degradeReason;
        public long repaintRequestedAtUtcMs;
        public long capturedAtUtcMs;
        public string originalError;
        public ulong instanceId;
        public float pixelsPerPoint;
        public string mappingEvidence;
    }

    public sealed class EditorWindowCaptureException : InvalidOperationException
    {
        public readonly EditorWindowPixelCapture Diagnostics;
        public EditorWindowCaptureException(string message, EditorWindowPixelCapture diagnostics) : base(message)
        {
            Diagnostics = diagnostics;
        }
    }

    /// <summary>
    /// 全窗口级布局诊断：每一区域的宽度、内容最大宽度、横向溢出检测；
    /// 供 MCP resource / tool 读取，用于自动化验收。
    /// </summary>
    [InitializeOnLoad]
    public static class UPilotWindowDiagnostics
    {
        internal static Func<EditorWindow, bool> RejectNativeCaptureForTesting;
        private const string DomainReloadTsKey = "UPilot.DomainReloadTimestamp";

        static UPilotWindowDiagnostics()
        {
            SessionState.SetInt(ProjectSessionKey(DomainReloadTsKey), (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000));
        }

        public static int DomainReloadEpoch => SessionState.GetInt(ProjectSessionKey(DomainReloadTsKey), 0);

        // ── Per-section snapshots ────────────────────────────────────────────

        public static float  WindowWidth;
        public static float  WindowHeight;
        public static int    ActiveTab;
        public static long   UpdatedUnixMs;
        public static bool   WindowOpen;

        public static readonly Dictionary<string, SectionSnapshot> Sections = new();

        public struct SectionSnapshot
        {
            public float DesiredWidth;
            public float AllocatedWidth;
            public bool  OverflowRisk;
        }

        public static void RecordWindow(float w, float h, int tab)
        {
            WindowWidth   = w;
            WindowHeight  = h;
            ActiveTab     = tab;
            WindowOpen    = true;
            UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public static void RecordSection(string name, float desiredW, float allocatedW)
        {
            Sections[name] = new SectionSnapshot
            {
                DesiredWidth  = desiredW,
                AllocatedWidth = allocatedW,
                OverflowRisk  = desiredW > allocatedW + 1f,
            };
        }

        public static void OnWindowClosed()
        {
            WindowOpen = false;
            Sections.Clear();
        }

        // ── Health score ─────────────────────────────────────────────────────

        public static string ComputeHealthScore()
        {
            if (!WindowOpen) return "unknown";
            foreach (var kv in Sections)
            {
                if (kv.Value.OverflowRisk) return "fail";
            }
            return "ok";
        }

        // ── Code version (assembly) ──────────────────────────────────────────

        private static string _cachedCodeVersion;

        private static string ProjectSessionKey(string key)
        {
            return UPilotPreferences.ProjectKey(key);
        }

        public static string CodeVersion
        {
            get
            {
                if (_cachedCodeVersion != null) return _cachedCodeVersion;
                var asm = typeof(UPilotWindowDiagnostics).Assembly;
                var name = asm.GetName();
                _cachedCodeVersion = $"{name.Name}@{name.Version}";
                return _cachedCodeVersion;
            }
        }

        // ── Editor window screenshot (Windows) ──────────────────────────────

        public static string CaptureEditorWindowBase64(string windowTitle)
        {
#if UNITY_EDITOR_WIN
            return CaptureEditorWindowWin(windowTitle);
#else
            return null;
#endif
        }

        public static string CaptureEditorWindowBase64(EditorWindow window)
        {
#if UNITY_EDITOR_WIN
            return CaptureEditorWindowPixels(window)?.imageData;
#else
            return null;
#endif
        }

        public static EditorWindowPixelCapture CaptureEditorWindowPixels(EditorWindow window, bool allowFallback = false)
        {
#if UNITY_EDITOR_WIN
            return CaptureEditorWindowWin(window, allowFallback);
#else
            return null;
#endif
        }

        public static bool TryGetEditorWindowMinimized(
            EditorWindow window,
            out bool minimized,
            out long windowHandle)
        {
            minimized = false;
            windowHandle = 0;
#if UNITY_EDITOR_WIN
            if (window == null) return false;
            var pos = window.position;
            float scale = Mathf.Max(1f, EditorGUIUtility.pixelsPerPoint);
            var target = new NativeRect
            {
                Left = Mathf.RoundToInt(pos.x * scale),
                Top = Mathf.RoundToInt(pos.y * scale),
                Right = Mathf.RoundToInt((pos.x + pos.width) * scale),
                Bottom = Mathf.RoundToInt((pos.y + pos.height) * scale),
            };
            var hwnd = FindBestUnityWindow(target, Process.GetCurrentProcess().Id, includeMinimized: true);
            if (hwnd == IntPtr.Zero) return false;
            windowHandle = hwnd.ToInt64();
            minimized = IsIconic(hwnd);
            return true;
#else
            return false;
#endif
        }

        public static bool TryGetUnityEditorMinimized(out bool minimized, out long windowHandle)
        {
            minimized = false;
            windowHandle = 0;
#if UNITY_EDITOR_WIN
            var hwnd = Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd == IntPtr.Zero) return false;
            windowHandle = hwnd.ToInt64();
            minimized = IsIconic(hwnd);
            return true;
#else
            return false;
#endif
        }

#if UNITY_EDITOR_WIN
        private static string CaptureEditorWindowWin(string windowTitle)
        {
            try
            {
                var win = UPilotPlayInputService.FindTargetWindow(windowTitle);
                return CaptureEditorWindowWin(win)?.imageData;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UPilot] Editor window capture failed: {ex.Message}");
                return null;
            }
        }

        private static EditorWindowPixelCapture CaptureEditorWindowWin(EditorWindow win, bool allowFallback = false)
        {
            try
            {
                if (win == null) return null;

                long repaintAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                win.Repaint();

                var pos = win.position;
                float scale = Mathf.Max(1f, EditorGUIUtility.pixelsPerPoint);
                var target = new NativeRect
                {
                    Left = Mathf.RoundToInt(pos.x * scale),
                    Top = Mathf.RoundToInt(pos.y * scale),
                    Right = Mathf.RoundToInt((pos.x + pos.width) * scale),
                    Bottom = Mathf.RoundToInt((pos.y + pos.height) * scale),
                };

                IntPtr hwnd = FindMappedUnityWindow(win, target, out var mappingEvidence);
                string nativeError = hwnd == IntPtr.Zero ? "WINDOW_MAPPING_UNVERIFIED" : "PRINT_WINDOW_FAILURE_INJECTED";
                if (hwnd != IntPtr.Zero && RejectNativeCaptureForTesting?.Invoke(win) != true
                    && TryPrintWindowCrop(hwnd, target, out byte[] nativePng, out int width, out int height, out NativeRect hwndRect, out nativeError))
                {
                    if (win == null || win.position != pos || FindMappedUnityWindow(win, target, out _) != hwnd)
                        throw new EditorWindowCaptureException("Window identity or geometry changed during capture.", new EditorWindowPixelCapture
                        { windowHandle = hwnd.ToInt64(), captureApi = "Win32.PrintWindow", originalError = "WINDOW_CHANGED_DURING_CAPTURE" });
                    return new EditorWindowPixelCapture
                    {
                        imageData = Convert.ToBase64String(nativePng),
                        width = width,
                        height = height,
                        captureApi = "Win32.PrintWindow(PW_RENDERFULLCONTENT)",
                        windowHandle = hwnd.ToInt64(),
                        unityProcessId = Process.GetCurrentProcess().Id,
                        windowLeft = hwndRect.Left,
                        windowTop = hwndRect.Top,
                        windowRight = hwndRect.Right,
                        windowBottom = hwndRect.Bottom,
                        foreground = GetForegroundWindow() == hwnd,
                        occlusionSensitive = false,
                        pixelSourceVerified = true,
                        degraded = false,
                        degradeReason = string.Empty,
                        repaintRequestedAtUtcMs = repaintAt,
                        capturedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        instanceId = UPilotEntityIds.ToWireId(win),
                        pixelsPerPoint = scale,
                        mappingEvidence = mappingEvidence,
                    };
                }

                var failure = new EditorWindowPixelCapture
                {
                    instanceId = UPilotEntityIds.ToWireId(win), pixelsPerPoint = scale,
                    windowHandle = hwnd.ToInt64(), unityProcessId = Process.GetCurrentProcess().Id,
                    captureApi = "Win32.PrintWindow(PW_RENDERFULLCONTENT)",
                    windowLeft = target.Left, windowTop = target.Top, windowRight = target.Right, windowBottom = target.Bottom,
                    originalError = nativeError,
                    mappingEvidence = mappingEvidence,
                };
                if (!allowFallback)
                    throw new EditorWindowCaptureException(failure.originalError + "; screen fallback is disabled. " + mappingEvidence, failure);

                int px = Mathf.RoundToInt(pos.x);
                int py = Mathf.RoundToInt(pos.y);
                int pw = Mathf.Max(1, Mathf.RoundToInt(pos.width));
                int ph = Mathf.Max(1, Mathf.RoundToInt(pos.height));

                Color[] pixels = UnityEditorInternal.InternalEditorUtility.ReadScreenPixel(
                    new Vector2(px, py), pw, ph);

                var tex = new Texture2D(pw, ph, TextureFormat.RGBA32, false);
                tex.SetPixels(pixels);
                tex.Apply();
                byte[] png = tex.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(tex);
                return new EditorWindowPixelCapture
                {
                    imageData = Convert.ToBase64String(png),
                    width = pw,
                    height = ph,
                    captureApi = "InternalEditorUtility.ReadScreenPixel",
                    windowHandle = hwnd.ToInt64(),
                    unityProcessId = Process.GetCurrentProcess().Id,
                    foreground = hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd,
                    occlusionSensitive = true,
                    pixelSourceVerified = false,
                    degraded = true,
                    degradeReason = "Native offscreen capture unavailable; screen pixels may be occluded by another window.",
                    repaintRequestedAtUtcMs = repaintAt,
                    capturedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    originalError = failure.originalError,
                    instanceId = failure.instanceId,
                    pixelsPerPoint = scale,
                };
            }
            catch (EditorWindowCaptureException) { throw; }
            catch (Exception ex)
            {
                throw new EditorWindowCaptureException(ex.Message, new EditorWindowPixelCapture
                {
                    captureApi = "EditorWindow", originalError = ex.GetType().Name + ": " + ex.Message,
                    instanceId = win == null ? 0 : UPilotEntityIds.ToWireId(win),
                    unityProcessId = Process.GetCurrentProcess().Id,
                    pixelsPerPoint = Mathf.Max(1f, EditorGUIUtility.pixelsPerPoint),
                });
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo { public BitmapInfoHeader bmiHeader; public uint bmiColors; }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        private const uint BiRgb = 0;
        private const uint DibRgbColors = 0;
        private const uint PwRenderFullContent = 2;
        private const int Srccopy = 0x00CC0020;

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

        private static IntPtr FindBestUnityWindow(NativeRect target, int processId, bool includeMinimized = false)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = long.MaxValue;
            int cx = target.Left + Math.Max(1, target.Right - target.Left) / 2;
            int cy = target.Top + Math.Max(1, target.Bottom - target.Top) / 2;
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint owner);
                if (owner != (uint)processId || (!includeMinimized && !IsWindowVisible(hwnd)) || !GetWindowRect(hwnd, out NativeRect rect)) return true;
                if (cx < rect.Left || cx >= rect.Right || cy < rect.Top || cy >= rect.Bottom) return true;
                long area = Math.Max(1, rect.Right - rect.Left) * (long)Math.Max(1, rect.Bottom - rect.Top);
                if (area < bestArea) { best = hwnd; bestArea = area; }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        private static object ReadWindowMember(object target, string name)
        {
            for (var type = target?.GetType(); type != null; type = type.BaseType)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                var field = type.GetField(name, flags);
                if (field != null) return field.GetValue(target);
                var property = type.GetProperty(name, flags);
                if (property != null) return property.GetValue(target);
            }
            return null;
        }

        private static IntPtr FindMappedUnityWindow(EditorWindow window, NativeRect target, out string evidence)
        {
            evidence = "Unity GUIView/ContainerWindow mapping unavailable";
            // Bind to the owning Unity container, not the smallest OS rectangle under the target's center.
            var view = ReadWindowMember(window, "m_Parent");
            var actualView = ReadWindowMember(view, "actualView") as EditorWindow;
            if (actualView != null && actualView != window) return IntPtr.Zero;
            var container = ReadWindowMember(view, "window");
            if (!(ReadWindowMember(container, "position") is Rect position)) return IntPtr.Zero;
            float scale = Mathf.Max(1f, EditorGUIUtility.pixelsPerPoint);
            var expected = new NativeRect
            {
                Left = Mathf.RoundToInt(position.x * scale), Top = Mathf.RoundToInt(position.y * scale),
                Right = Mathf.RoundToInt(position.xMax * scale), Bottom = Mathf.RoundToInt(position.yMax * scale),
            };
            IntPtr match = IntPtr.Zero;
            int count = 0;
            var candidates = new List<string>();
            EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out uint owner);
                if (owner != (uint)Process.GetCurrentProcess().Id || !IsWindowVisible(handle) || IsIconic(handle)
                    || !GetWindowRect(handle, out var outer)) return true;
                var origin = new NativePoint();
                if (!GetClientRect(handle, out var client) || !ClientToScreen(handle, ref origin)) return true;
                client.Right += origin.X; client.Bottom += origin.Y; client.Left = origin.X; client.Top = origin.Y;
                if (candidates.Count < 16)
                    candidates.Add(handle.ToInt64() + ":outer=" + RectText(outer) + ",client=" + RectText(client));
                if (!Contains(outer, target)) return true;
                if (!SameRect(expected, outer) && !SameRect(expected, client)) return true;
                match = handle;
                count++;
                return true;
            }, IntPtr.Zero);
            evidence = "target=" + RectText(target) + ";container=" + RectText(expected) + ";matches=" + count
                + ";pid=" + Process.GetCurrentProcess().Id + ";candidates=[" + string.Join(";", candidates) + "]";
            return count == 1 ? match : IntPtr.Zero;
        }

        private static string RectText(NativeRect rect) => $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}";
        private static bool Contains(NativeRect outer, NativeRect inner) =>
            inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

        private static bool SameRect(NativeRect a, NativeRect b) =>
            a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

        private static bool TryPrintWindowCrop(IntPtr hwnd, NativeRect target, out byte[] png, out int cropWidth, out int cropHeight, out NativeRect hwndRect, out string originalError)
        {
            png = null;
            originalError = "WINDOW_GEOMETRY_UNVERIFIED";
            cropWidth = cropHeight = 0;
            hwndRect = default;
            if (!GetWindowRect(hwnd, out hwndRect)) return false;
            if (!Contains(hwndRect, target)) return false;
            int width = Math.Max(1, hwndRect.Right - hwndRect.Left);
            int height = Math.Max(1, hwndRect.Bottom - hwndRect.Top);
            var info = new BitmapInfo
            {
                bmiHeader = new BitmapInfoHeader
                {
                    biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(), biWidth = width, biHeight = -height,
                    biPlanes = 1, biBitCount = 32, biCompression = BiRgb, biSizeImage = (uint)(width * height * 4),
                }
            };
            IntPtr windowDc = GetWindowDC(hwnd);
            IntPtr memoryDc = IntPtr.Zero;
            IntPtr bitmap = IntPtr.Zero;
            IntPtr previous = IntPtr.Zero;
            try
            {
                originalError = "GDI_CAPTURE_BUFFER_UNAVAILABLE";
                memoryDc = CreateCompatibleDC(windowDc);
                bitmap = CreateDIBSection(windowDc, ref info, DibRgbColors, out IntPtr bits, IntPtr.Zero, 0);
                if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero || bits == IntPtr.Zero) return false;
                previous = SelectObject(memoryDc, bitmap);
                if (!PrintWindow(hwnd, memoryDc, PwRenderFullContent))
                {
                    // Read immediately, before GDI cleanup or other P/Invoke calls replace the error.
                    originalError = "PRINT_WINDOW_FAILED:" + Marshal.GetLastWin32Error();
                    return false;
                }
                originalError = "";
                var bgra = new byte[width * height * 4];
                Marshal.Copy(bits, bgra, 0, bgra.Length);
                int left = Mathf.Clamp(target.Left - hwndRect.Left, 0, width - 1);
                int top = Mathf.Clamp(target.Top - hwndRect.Top, 0, height - 1);
                int right = Mathf.Clamp(target.Right - hwndRect.Left, left + 1, width);
                int bottom = Mathf.Clamp(target.Bottom - hwndRect.Top, top + 1, height);
                cropWidth = right - left;
                cropHeight = bottom - top;
                var rgba = new byte[cropWidth * cropHeight * 4];
                for (int y = 0; y < cropHeight; y++)
                {
                    for (int x = 0; x < cropWidth; x++)
                    {
                        int src = ((top + y) * width + left + x) * 4;
                        int dst = ((cropHeight - 1 - y) * cropWidth + x) * 4;
                        rgba[dst] = bgra[src + 2]; rgba[dst + 1] = bgra[src + 1]; rgba[dst + 2] = bgra[src]; rgba[dst + 3] = 255;
                    }
                }
                var texture = new Texture2D(cropWidth, cropHeight, TextureFormat.RGBA32, false);
                texture.LoadRawTextureData(rgba);
                texture.Apply();
                png = texture.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(texture);
                return png != null && png.Length > 0;
            }
            finally
            {
                if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero) SelectObject(memoryDc, previous);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
                if (windowDc != IntPtr.Zero) ReleaseDC(hwnd, windowDc);
            }
        }
#endif
    }
}
