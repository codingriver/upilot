// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace CodingRiver.UPilot.Execution
{
    /// <summary>
    /// Thread-safe, bounded caches for reflection metadata. Static state is
    /// naturally discarded by Unity Domain Reload.
    /// </summary>
    public static class ReflectionCache
    {
        public const int DefaultMaxTypeEntries = 512;
        public const int DefaultMaxMethodEntries = 1024;

        private sealed class TypeCacheEntry
        {
            public Type Value;
            public LinkedListNode<string> LruNode;
        }

        private sealed class MethodCacheEntry
        {
            public MethodInfo[] Value;
            public LinkedListNode<string> LruNode;
        }

        private static readonly object TypeGate = new object();
        private static readonly Dictionary<string, TypeCacheEntry> TypesByName =
            new Dictionary<string, TypeCacheEntry>(StringComparer.Ordinal);
        private static readonly LinkedList<string> TypeLru = new LinkedList<string>();

        private static readonly object MethodGate = new object();
        private static readonly Dictionary<string, MethodCacheEntry> MethodsByKey =
            new Dictionary<string, MethodCacheEntry>(StringComparer.Ordinal);
        private static readonly LinkedList<string> MethodLru = new LinkedList<string>();

        private static volatile bool _enabled = true;
        private static int _maxTypeEntries = DefaultMaxTypeEntries;
        private static int _maxMethodEntries = DefaultMaxMethodEntries;
        private static long _typeCacheHits;
        private static long _typeCacheMisses;
        private static long _typeCacheEvictions;
        private static long _methodCacheHits;
        private static long _methodCacheMisses;
        private static long _methodCacheEvictions;

        public static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public static int MaxTypeEntries
        {
            get
            {
                lock (TypeGate) return _maxTypeEntries;
            }
            set
            {
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), "Cache capacity must be positive.");
                lock (TypeGate)
                {
                    _maxTypeEntries = value;
                    while (TypesByName.Count > _maxTypeEntries) EvictOldestType();
                }
            }
        }

        public static int MaxMethodEntries
        {
            get
            {
                lock (MethodGate) return _maxMethodEntries;
            }
            set
            {
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), "Cache capacity must be positive.");
                lock (MethodGate)
                {
                    _maxMethodEntries = value;
                    while (MethodsByKey.Count > _maxMethodEntries) EvictOldestMethod();
                }
            }
        }

        /// <summary>
        /// Finds the first exact full-name/assembly-qualified match, then the
        /// first short-name match. Only successful lookups are cached so types
        /// created later in the same Domain remain discoverable.
        /// </summary>
        public static Type FindType(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            typeName = typeName.Trim();
            if (!Enabled) return FindTypeUncached(typeName);

            lock (TypeGate)
            {
                if (TypesByName.TryGetValue(typeName, out var cached))
                {
                    Touch(TypeLru, cached.LruNode);
                    _typeCacheHits++;
                    return cached.Value;
                }
                _typeCacheMisses++;
            }

            var found = FindTypeUncached(typeName);
            if (found == null) return null;

            lock (TypeGate)
            {
                if (TypesByName.TryGetValue(typeName, out var existing))
                {
                    Touch(TypeLru, existing.LruNode);
                    return existing.Value;
                }

                while (TypesByName.Count >= _maxTypeEntries) EvictOldestType();
                var node = TypeLru.AddLast(typeName);
                TypesByName[typeName] = new TypeCacheEntry { Value = found, LruNode = node };
                return found;
            }
        }

        /// <summary>
        /// Returns all exact matches, or all short-name matches when no exact
        /// match exists. Ambiguity queries intentionally remain uncached so
        /// dynamically emitted types are visible in the current Domain.
        /// </summary>
        public static List<Type> FindTypes(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return new List<Type>();
            typeName = typeName.Trim();
            var exact = new List<Type>();
            var shortMatches = new List<Type>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var type in GetLoadableTypes(assembly))
                {
                    if (string.Equals(type.FullName, typeName, StringComparison.Ordinal) ||
                        string.Equals(type.AssemblyQualifiedName, typeName, StringComparison.Ordinal))
                        exact.Add(type);
                    else if (string.Equals(type.Name, typeName, StringComparison.Ordinal))
                        shortMatches.Add(type);
                }
            }

            return exact.Count > 0 ? exact : shortMatches;
        }

        private static Type FindTypeUncached(string typeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var exact = assembly.GetType(typeName, false, false);
                    if (exact != null) return exact;
                }
                catch { /* skip unavailable assembly metadata */ }
            }

            if (typeName.IndexOf(',') >= 0)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    foreach (var type in GetLoadableTypes(assembly))
                        if (string.Equals(type.AssemblyQualifiedName, typeName, StringComparison.Ordinal)) return type;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                foreach (var type in GetLoadableTypes(assembly))
                    if (string.Equals(type.Name, typeName, StringComparison.Ordinal)) return type;

            return null;
        }

        private static Type[] GetLoadableTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(type => type != null).ToArray(); }
            catch { return Array.Empty<Type>(); }
        }

        public static string MethodCacheKey(Type type, string methodName, BindingFlags flags)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            return $"{type.AssemblyQualifiedName}|{methodName}|{(int)flags}";
        }

        /// <summary>
        /// Returns the cached method candidates for one type/name/flags tuple.
        /// Callers must treat the returned array as read-only.
        /// </summary>
        public static MethodInfo[] GetMethods(Type type, string methodName, BindingFlags flags)
        {
            if (type == null || string.IsNullOrEmpty(methodName)) return Array.Empty<MethodInfo>();
            if (!Enabled) return FindMethodsUncached(type, methodName, flags);

            var key = MethodCacheKey(type, methodName, flags);
            lock (MethodGate)
            {
                if (MethodsByKey.TryGetValue(key, out var cached))
                {
                    Touch(MethodLru, cached.LruNode);
                    _methodCacheHits++;
                    return cached.Value;
                }
                _methodCacheMisses++;
            }

            var found = FindMethodsUncached(type, methodName, flags);
            lock (MethodGate)
            {
                if (MethodsByKey.TryGetValue(key, out var existing))
                {
                    Touch(MethodLru, existing.LruNode);
                    return existing.Value;
                }

                while (MethodsByKey.Count >= _maxMethodEntries) EvictOldestMethod();
                var node = MethodLru.AddLast(key);
                MethodsByKey[key] = new MethodCacheEntry { Value = found, LruNode = node };
                return found;
            }
        }

        private static MethodInfo[] FindMethodsUncached(Type type, string methodName, BindingFlags flags)
        {
            return type.GetMethods(flags).Where(method => method.Name == methodName).ToArray();
        }

        private static void Touch(LinkedList<string> lru, LinkedListNode<string> node)
        {
            if (node.List == null || ReferenceEquals(lru.Last, node)) return;
            lru.Remove(node);
            lru.AddLast(node);
        }

        private static void EvictOldestType()
        {
            var node = TypeLru.First;
            if (node == null) return;
            TypeLru.RemoveFirst();
            TypesByName.Remove(node.Value);
            _typeCacheEvictions++;
        }

        private static void EvictOldestMethod()
        {
            var node = MethodLru.First;
            if (node == null) return;
            MethodLru.RemoveFirst();
            MethodsByKey.Remove(node.Value);
            _methodCacheEvictions++;
        }

        public static CacheStats GetStats()
        {
            lock (TypeGate)
            lock (MethodGate)
            {
                var totalHits = _typeCacheHits + _methodCacheHits;
                var totalMisses = _typeCacheMisses + _methodCacheMisses;
                var total = totalHits + totalMisses;
                return new CacheStats
                {
                    TypeCacheSize = TypesByName.Count,
                    TypeCacheHits = _typeCacheHits,
                    TypeCacheMisses = _typeCacheMisses,
                    TypeCacheEvictions = _typeCacheEvictions,
                    MethodCacheSize = MethodsByKey.Count,
                    MethodCacheHits = _methodCacheHits,
                    MethodCacheMisses = _methodCacheMisses,
                    MethodCacheEvictions = _methodCacheEvictions,
                    TotalHits = totalHits,
                    TotalMisses = totalMisses,
                    HitRate = total > 0 ? (double)totalHits / total : 0.0,
                };
            }
        }

        public static void Clear()
        {
            lock (TypeGate)
            lock (MethodGate)
            {
                TypesByName.Clear();
                TypeLru.Clear();
                MethodsByKey.Clear();
                MethodLru.Clear();
                _typeCacheHits = 0;
                _typeCacheMisses = 0;
                _typeCacheEvictions = 0;
                _methodCacheHits = 0;
                _methodCacheMisses = 0;
                _methodCacheEvictions = 0;
            }
        }
    }

    [Serializable]
    public sealed class CacheStats
    {
        public int TypeCacheSize;
        public long TypeCacheHits;
        public long TypeCacheMisses;
        public long TypeCacheEvictions;
        public int MethodCacheSize;
        public long MethodCacheHits;
        public long MethodCacheMisses;
        public long MethodCacheEvictions;
        public long TotalHits;
        public long TotalMisses;
        public double HitRate;
    }
}
