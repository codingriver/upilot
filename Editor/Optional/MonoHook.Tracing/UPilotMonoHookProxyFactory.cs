// -----------------------------------------------------------------------
// UPilot Editor - isolated lifecycle trampoline/proxy factory.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;

namespace CodingRiver.UPilot
{
    /// <summary>
    /// Creates one proxy method per distinct target MethodBase. MonoHook patches
    /// the proxy method in-place with that target's original instructions, so a
    /// shared proxy would make the last installed lifecycle type overwrite all
    /// earlier targets.
    /// </summary>
    internal static class UPilotMonoHookProxyFactory
    {
        internal sealed class ProxyEntry
        {
            public MethodInfo Method { get; }
            public string Key { get; }

            internal ProxyEntry(MethodInfo method, string key)
            {
                Method = method;
                Key = key;
            }
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, ProxyEntry> Entries =
            new Dictionary<string, ProxyEntry>(StringComparer.Ordinal);
        private static AssemblyBuilder _assembly;
        private static ModuleBuilder _module;
        private static int _nextTypeId;

        internal static ProxyEntry GetOrCreate(MethodBase target, MethodInfo template)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (template == null) throw new ArgumentNullException(nameof(template));

            string key = BuildTargetKey(target);
            lock (Sync)
            {
                if (Entries.TryGetValue(key, out var existing))
                    return existing;

                var proxy = CreateProxy(template, key);
                Entries.Add(key, proxy);
                return proxy;
            }
        }

        internal static void ClearUnused()
        {
            // Dynamic methods cannot be unloaded independently from the editor
            // AppDomain. Keeping the small per-target cache alive is intentional;
            // AssemblyReloadEvents clears it with the domain.
        }

        private static ProxyEntry CreateProxy(MethodInfo template, string key)
        {
            EnsureModule();
            int typeId = Interlocked.Increment(ref _nextTypeId);
            var typeBuilder = _module.DefineType(
                "UPilotTracerProxyType" + typeId,
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract);
            var parameters = template.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
            var methodBuilder = typeBuilder.DefineMethod(
                "Invoke",
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                CallingConventions.Standard,
                template.ReturnType,
                parameters);
            methodBuilder.SetImplementationFlags(
                MethodImplAttributes.Managed |
                MethodImplAttributes.NoOptimization |
                MethodImplAttributes.NoInlining);

            EmitPlaceholderBody(methodBuilder.GetILGenerator(), template.ReturnType);
            var createdType = typeBuilder.CreateType();
            var method = createdType.GetMethod(
                "Invoke",
                BindingFlags.Public | BindingFlags.Static);
            if (method == null)
                throw new MissingMethodException(createdType.FullName, "Invoke");

            RuntimeHelpers.PrepareMethod(method.MethodHandle);
            return new ProxyEntry(method, key);
        }

        private static void EnsureModule()
        {
            if (_module != null) return;
            var name = new AssemblyName("UPilot.Tracer.DynamicProxies");
            _assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
            _module = _assembly.DefineDynamicModule(name.Name);
        }

        private static void EmitPlaceholderBody(ILGenerator il, Type returnType)
        {
            // MethodHook validates a minimum managed body size before patching.
            for (int i = 0; i < 12; i++)
                il.Emit(OpCodes.Nop);

            if (returnType == typeof(void))
            {
                il.Emit(OpCodes.Ret);
                return;
            }

            if (returnType.IsValueType)
            {
                var local = il.DeclareLocal(returnType);
                il.Emit(OpCodes.Ldloca_S, local);
                il.Emit(OpCodes.Initobj, returnType);
                il.Emit(OpCodes.Ldloc, local);
            }
            else
            {
                il.Emit(OpCodes.Ldnull);
            }
            il.Emit(OpCodes.Ret);
        }

        private static string BuildTargetKey(MethodBase target)
        {
            string moduleId = target.Module?.ModuleVersionId.ToString("N") ?? string.Empty;
            string declaringType = target.DeclaringType?.AssemblyQualifiedName ?? string.Empty;
            string token = target.MetadataToken.ToString("X8");
            return moduleId + "|" + declaringType + "|" + token + "|" + target.Name;
        }
    }
}
