using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using System.Linq;
using System.IO;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MonoHook
{
    /// <summary>
    /// Hook 池，防止重复 Hook
    /// </summary>
    public static class HookPool
    {
        internal static readonly object SyncRoot = new object();
        private static Dictionary<MethodBase, MethodHook> _hooks = new Dictionary<MethodBase, MethodHook>();

        public static void AddHook(MethodBase method, MethodHook hook)
        {
            lock (SyncRoot)
            {
            MethodHook preHook;
            if (_hooks.TryGetValue(method, out preHook))
            {
                if (!ReferenceEquals(preHook, hook))
                    throw new System.InvalidOperationException("Target already has a different Hook owner; explicit uninstall is required.");
            }
            else
                _hooks.Add(method, hook);
            }
        }

        public static MethodHook GetHook(MethodBase method)
        {
            if (method == null) return null;

            lock (SyncRoot)
            {
                MethodHook hook;
                if (_hooks.TryGetValue(method, out hook)) return hook;
                return null;
            }
        }

        public static void RemoveHooker(MethodBase method)
        {
            if (method == null) return;

            lock (SyncRoot) _hooks.Remove(method);
        }

        public static void UninstallAll()
        {
            var list = GetAllHooks();
            foreach (var hook in list)
                hook.Uninstall();

        }

        public static void UninstallByTag(string tag)
        {
            var list = GetAllHooks();
            foreach (var hook in list)
            {
                if(hook.tag == tag)
                    hook.Uninstall();
            }
        }

        public static List<MethodHook> GetAllHooks()
        {
            lock (SyncRoot) return _hooks.Values.ToList();
        }
    }

}
