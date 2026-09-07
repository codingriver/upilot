using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotMenuTests
    {
        private static readonly HashSet<string> AllowedMenuPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            "UPilot/打开 UPilot",
            "UPilot/高级设置",
            "UPilot/Flow/Test Runner",
            "UPilot/Flow/Settings",
            "UPilot/Flow/Enable",
            "UPilot/Flow/Disable",
            "UPilot/追踪器",
        };

        [Test]
        public void ProductMenuUsesConsolidatedInformationArchitecture()
        {
            HashSet<string> paths = CollectProductMenuPaths();

            Assert.That(paths, Does.Contain("UPilot/打开 UPilot"));
            Assert.That(paths, Does.Contain("UPilot/高级设置"));
            Assert.That(paths, Does.Contain("UPilot/Flow/Enable"));
            Assert.That(paths, Does.Contain("UPilot/Flow/Disable"));

            foreach (string path in paths)
                Assert.That(AllowedMenuPaths, Does.Contain(path), "Unexpected UPilot menu item: " + path);

            Assert.That(paths, Does.Not.Contain("UPilot/UPilot"));

            AssertOptionalMenuPath(paths, "CodingRiver.UPilot.Flow.TestRunnerWindow", "UPilot/Flow/Test Runner");
            AssertOptionalMenuPath(paths, "CodingRiver.UPilot.Flow.UPilotFlowMenuItems", "UPilot/Flow/Settings");
            AssertOptionalMenuPath(paths, "CodingRiver.UPilot.UPilotMonoHookWindow", "UPilot/追踪器");
        }

        private static void AssertOptionalMenuPath(HashSet<string> paths, string typeName, string expectedPath)
        {
            if (FindLoadedType(typeName) != null)
                Assert.That(paths, Does.Contain(expectedPath));
        }

        private static HashSet<string> CollectProductMenuPaths()
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (Type type in GetLoadableTypes(assembly))
                {
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        foreach (MenuItem attribute in method.GetCustomAttributes(typeof(MenuItem), false))
                        {
                            if (!attribute.validate && attribute.menuItem.StartsWith("UPilot/", StringComparison.Ordinal))
                                result.Add(attribute.menuItem);
                        }
                    }
                }
            }

            return result;
        }

        private static Type FindLoadedType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null)
                    return type;
            }

            return null;
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var result = new List<Type>();
                foreach (Type type in ex.Types)
                {
                    if (type != null)
                        result.Add(type);
                }

                return result;
            }
        }
    }
}
