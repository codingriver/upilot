using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace CodingRiver.UPilot.Execution
{
    /// <summary>
    /// Domain-local emitted delegate cache for validated C# subset programs.
    /// The emitted entry point is deliberately opaque: callers cannot supply IL
    /// and every delegate remains bound to the already parsed/validated AST.
    /// </summary>
    public static class CSharpEmitBackend
    {
        private sealed class Entry
        {
            public CSharpProgram Program;
            public Func<CSharpEvaluationContext, CSharpEvaluationResult> Delegate;
            public Func<CSharpEvaluationContext, Task<CSharpEvaluationResult>> AsyncDelegate;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public static string CacheKey(string code, string mode, IEnumerable<string> imports)
        {
            string canonical = CSharpSubsetEngine.LanguageProfile + "\n" + (mode ?? "auto").Trim().ToLowerInvariant() + "\n" +
                               string.Join(";", imports ?? Array.Empty<string>()) + "\n" + (code ?? "");
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)).Select(value => value.ToString("x2")));
        }

        public static bool IsCached(string key)
        {
            lock (Gate) return !string.IsNullOrWhiteSpace(key) && Entries.ContainsKey(key);
        }

        public static Func<CSharpEvaluationContext, CSharpEvaluationResult> Compile(
            string key,
            string code,
            string mode)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ExecutionContractException("CSHARP_EMIT_INVALID_CACHE_KEY", "Emit cache key is required.");
            lock (Gate)
            {
                Entries.TryGetValue(key, out var cached);
                if (cached?.Delegate != null) return cached.Delegate;
                var program = cached?.Program ?? CSharpSubsetEngine.Parse(code, mode);
                try
                {
                    var dynamic = new DynamicMethod(
                        "UPilot_CSharpEval_" + key.Substring(0, Math.Min(12, key.Length)),
                        typeof(CSharpEvaluationResult),
                        new[] { typeof(CSharpProgram), typeof(CSharpEvaluationContext) },
                        typeof(CSharpEmitBackend).Module,
                        true);
                    var il = dynamic.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Callvirt, typeof(CSharpProgram).GetMethod(nameof(CSharpProgram.Execute), BindingFlags.Public | BindingFlags.Instance));
                    il.Emit(OpCodes.Ret);
                    var compiled = (Func<CSharpEvaluationContext, CSharpEvaluationResult>)dynamic.CreateDelegate(
                        typeof(Func<CSharpEvaluationContext, CSharpEvaluationResult>), program);
                    if (cached == null) Entries[key] = new Entry { Program = program, Delegate = compiled };
                    else cached.Delegate = compiled;
                    return compiled;
                }
                catch (Exception ex)
                {
                    throw new ExecutionContractException("CSHARP_EMIT_COMPILE_FAILED", ex.GetType().FullName + ": " + ex.Message);
                }
            }
        }

        public static Func<CSharpEvaluationContext, Task<CSharpEvaluationResult>> CompileAsync(
            string key,
            string code,
            string mode)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ExecutionContractException("CSHARP_EMIT_INVALID_CACHE_KEY", "Emit cache key is required.");
            lock (Gate)
            {
                if (Entries.TryGetValue(key, out var cached) && cached.AsyncDelegate != null) return cached.AsyncDelegate;
                var program = cached?.Program ?? CSharpSubsetEngine.Parse(code, mode);
                try
                {
                    var dynamic = new DynamicMethod(
                        "UPilot_CSharpEvalAsync_" + key.Substring(0, Math.Min(12, key.Length)),
                        typeof(Task<CSharpEvaluationResult>),
                        new[] { typeof(CSharpProgram), typeof(CSharpEvaluationContext) },
                        typeof(CSharpEmitBackend).Module,
                        true);
                    var il = dynamic.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Callvirt, typeof(CSharpProgram).GetMethod(nameof(CSharpProgram.ExecuteAsync), BindingFlags.Public | BindingFlags.Instance));
                    il.Emit(OpCodes.Ret);
                    var compiled = (Func<CSharpEvaluationContext, Task<CSharpEvaluationResult>>)dynamic.CreateDelegate(
                        typeof(Func<CSharpEvaluationContext, Task<CSharpEvaluationResult>>), program);
                    if (cached == null) Entries[key] = new Entry { Program = program, AsyncDelegate = compiled };
                    else cached.AsyncDelegate = compiled;
                    return compiled;
                }
                catch (Exception ex)
                {
                    throw new ExecutionContractException("CSHARP_EMIT_COMPILE_FAILED", ex.GetType().FullName + ": " + ex.Message);
                }
            }
        }

        internal static void ValidateEmitSubset(string code)
        {
            CSharpSubsetEngine.Parse(code ?? "", "statements").ValidateSynchronousEmitProfile();
        }
    }
}
