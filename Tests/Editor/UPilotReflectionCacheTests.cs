// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CodingRiver.UPilot.Execution;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests
{
    [TestFixture]
    public sealed class UPilotReflectionCacheTests
    {
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;

        [SetUp]
        public void SetUp()
        {
            ReflectionCache.Enabled = true;
            ReflectionCache.MaxTypeEntries = ReflectionCache.DefaultMaxTypeEntries;
            ReflectionCache.MaxMethodEntries = ReflectionCache.DefaultMaxMethodEntries;
            ReflectionCache.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            ReflectionCache.Enabled = true;
            ReflectionCache.MaxTypeEntries = ReflectionCache.DefaultMaxTypeEntries;
            ReflectionCache.MaxMethodEntries = ReflectionCache.DefaultMaxMethodEntries;
            ReflectionCache.Clear();
        }

        [Test]
        public void FindTypeCachesFullShortAndAssemblyQualifiedNames()
        {
            Assert.That(ReflectionCache.FindType(typeof(string).FullName), Is.SameAs(typeof(string)));
            Assert.That(ReflectionCache.FindType(typeof(string).FullName), Is.SameAs(typeof(string)));
            Assert.That(ReflectionCache.FindType(nameof(ExecutionReflectionFixture)), Is.SameAs(typeof(ExecutionReflectionFixture)));
            Assert.That(ReflectionCache.FindType(nameof(ExecutionReflectionFixture)), Is.SameAs(typeof(ExecutionReflectionFixture)));
            Assert.That(ReflectionCache.FindType(typeof(int).AssemblyQualifiedName), Is.SameAs(typeof(int)));
            Assert.That(ReflectionCache.FindType(typeof(int).AssemblyQualifiedName), Is.SameAs(typeof(int)));

            var stats = ReflectionCache.GetStats();
            Assert.That(stats.TypeCacheSize, Is.EqualTo(3));
            Assert.That(stats.TypeCacheMisses, Is.EqualTo(3));
            Assert.That(stats.TypeCacheHits, Is.EqualTo(3));
        }

        [Test]
        public void FindTypesPreservesAmbiguityInformation()
        {
            var candidates = ReflectionCache.FindTypes("Object");

            Assert.That(candidates, Does.Contain(typeof(object)));
            Assert.That(candidates, Does.Contain(typeof(UnityEngine.Object)));
            Assert.That(candidates.Count, Is.GreaterThan(1));
        }

        [Test]
        public void MissingTypesAreNotCached()
        {
            const string missing = "CodingRiver.UPilot.Tests.TypeThatDoesNotExist";

            Assert.That(ReflectionCache.FindType(missing), Is.Null);
            Assert.That(ReflectionCache.FindType(missing), Is.Null);

            var stats = ReflectionCache.GetStats();
            Assert.That(stats.TypeCacheSize, Is.Zero);
            Assert.That(stats.TypeCacheMisses, Is.EqualTo(2));
            Assert.That(stats.TypeCacheHits, Is.Zero);
        }

        [Test]
        public void GetMethodsCachesByTypeNameAndFlags()
        {
            var first = ReflectionCache.GetMethods(typeof(ExecutionReflectionFixture), "Choose", PublicStatic);
            var second = ReflectionCache.GetMethods(typeof(ExecutionReflectionFixture), "Choose", PublicStatic);
            var differentFlags = ReflectionCache.GetMethods(
                typeof(ExecutionReflectionFixture), "Choose", BindingFlags.Public | BindingFlags.Instance);

            Assert.That(first, Is.Not.Empty);
            Assert.That(second, Is.SameAs(first));
            Assert.That(differentFlags, Is.Empty);
            var stats = ReflectionCache.GetStats();
            Assert.That(stats.MethodCacheSize, Is.EqualTo(2));
            Assert.That(stats.MethodCacheMisses, Is.EqualTo(2));
            Assert.That(stats.MethodCacheHits, Is.EqualTo(1));
        }

        [Test]
        public void MethodBinderUsesMethodCacheWithoutChangingBindingBehavior()
        {
            var first = MethodBinder.Bind(typeof(ExecutionReflectionFixture), "Choose", true,
                new[] { new ExecutionValue { Value = 3, DeclaredType = typeof(int) } });
            var second = MethodBinder.Bind(typeof(ExecutionReflectionFixture), "Choose", true,
                new[] { new ExecutionValue { Value = 4, DeclaredType = typeof(int) } });

            Assert.That(first.Method.GetParameters()[0].ParameterType, Is.EqualTo(typeof(int)));
            Assert.That(second.Method.GetParameters()[0].ParameterType, Is.EqualTo(typeof(int)));
            Assert.That(first.Arguments[0], Is.EqualTo(3));
            Assert.That(second.Arguments[0], Is.EqualTo(4));
            var stats = ReflectionCache.GetStats();
            Assert.That(stats.MethodCacheMisses, Is.EqualTo(1));
            Assert.That(stats.MethodCacheHits, Is.EqualTo(1));
        }

        [Test]
        public void LeastRecentlyUsedEntriesAreEvictedAtCapacity()
        {
            ReflectionCache.MaxTypeEntries = 2;
            ReflectionCache.FindType(typeof(string).FullName);
            ReflectionCache.FindType(typeof(int).FullName);
            ReflectionCache.FindType(typeof(string).FullName);
            ReflectionCache.FindType(typeof(bool).FullName);

            var afterTypeEviction = ReflectionCache.GetStats();
            Assert.That(afterTypeEviction.TypeCacheSize, Is.EqualTo(2));
            Assert.That(afterTypeEviction.TypeCacheEvictions, Is.EqualTo(1));
            ReflectionCache.FindType(typeof(int).FullName);
            Assert.That(ReflectionCache.GetStats().TypeCacheMisses, Is.EqualTo(4));

            ReflectionCache.Clear();
            ReflectionCache.MaxMethodEntries = 2;
            ReflectionCache.GetMethods(typeof(ExecutionReflectionFixture), "Choose", PublicStatic);
            ReflectionCache.GetMethods(typeof(ExecutionReflectionFixture), "Identity", PublicStatic);
            ReflectionCache.GetMethods(typeof(ExecutionReflectionFixture), "Choose", PublicStatic);
            ReflectionCache.GetMethods(typeof(ExecutionReflectionFixture), "Named", PublicStatic);

            var afterMethodEviction = ReflectionCache.GetStats();
            Assert.That(afterMethodEviction.MethodCacheSize, Is.EqualTo(2));
            Assert.That(afterMethodEviction.MethodCacheEvictions, Is.EqualTo(1));
            ReflectionCache.GetMethods(typeof(ExecutionReflectionFixture), "Identity", PublicStatic);
            Assert.That(ReflectionCache.GetStats().MethodCacheMisses, Is.EqualTo(4));
        }

        [Test]
        public void DisabledCacheBypassesStorageAndStatistics()
        {
            ReflectionCache.Enabled = false;

            ReflectionCache.FindType(typeof(string).FullName);
            ReflectionCache.FindType(typeof(string).FullName);
            ReflectionCache.GetMethods(typeof(ExecutionReflectionFixture), "Choose", PublicStatic);
            ReflectionCache.GetMethods(typeof(ExecutionReflectionFixture), "Choose", PublicStatic);

            var stats = ReflectionCache.GetStats();
            Assert.That(stats.TypeCacheSize, Is.Zero);
            Assert.That(stats.MethodCacheSize, Is.Zero);
            Assert.That(stats.TotalHits, Is.Zero);
            Assert.That(stats.TotalMisses, Is.Zero);
        }

        [Test]
        public void ClearResetsCachesAndStatistics()
        {
            ReflectionCache.FindType(typeof(string).FullName);
            ReflectionCache.FindType(typeof(string).FullName);
            ReflectionCache.GetMethods(typeof(ExecutionReflectionFixture), "Choose", PublicStatic);
            ReflectionCache.GetMethods(typeof(ExecutionReflectionFixture), "Choose", PublicStatic);

            ReflectionCache.Clear();

            var stats = ReflectionCache.GetStats();
            Assert.That(stats.TypeCacheSize, Is.Zero);
            Assert.That(stats.MethodCacheSize, Is.Zero);
            Assert.That(stats.TotalHits, Is.Zero);
            Assert.That(stats.TotalMisses, Is.Zero);
            Assert.That(stats.TypeCacheEvictions, Is.Zero);
            Assert.That(stats.MethodCacheEvictions, Is.Zero);
        }

        [Test]
        public void PrimedCachesSupportConcurrentReaders()
        {
            ReflectionCache.FindType(typeof(ExecutionReflectionFixture).FullName);
            ReflectionCache.GetMethods(typeof(ExecutionReflectionFixture), "Choose", PublicStatic);
            var failures = new ConcurrentQueue<Exception>();

            Parallel.For(0, 64, _ =>
            {
                try
                {
                    Assert.That(ReflectionCache.FindType(typeof(ExecutionReflectionFixture).FullName),
                        Is.SameAs(typeof(ExecutionReflectionFixture)));
                    Assert.That(ReflectionCache.GetMethods(typeof(ExecutionReflectionFixture), "Choose", PublicStatic).Length,
                        Is.EqualTo(2));
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            });

            Assert.That(failures, Is.Empty);
            var stats = ReflectionCache.GetStats();
            Assert.That(stats.TypeCacheHits, Is.EqualTo(64));
            Assert.That(stats.MethodCacheHits, Is.EqualTo(64));
            Assert.That(stats.TotalHits, Is.EqualTo(128));
        }
    }
}
