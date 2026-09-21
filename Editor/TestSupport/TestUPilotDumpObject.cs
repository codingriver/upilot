// -----------------------------------------------------------------------
// UPilot Editor - Object dump capability sample
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public enum TestUPilotDumpEnum
    {
        None,
        Ready,
        Completed,
    }

    public class TestUPilotDumpGrandParent
    {
        private int GrandParentPrivateInt = 101;
        protected string GrandParentProtectedString = "grand-parent protected";
        public long GrandParentPublicLong = 1001L;

        public string GrandParentPublicProperty => "grand-parent property";

        public int ReadGrandParentPrivateIntForTest() => GrandParentPrivateInt;
    }

    public class TestUPilotDumpParent : TestUPilotDumpGrandParent
    {
        private Guid ParentPrivateGuid = new Guid("11111111-2222-3333-4444-555555555555");
        protected decimal ParentProtectedDecimal = 12.34m;
        public DateTime ParentPublicDateTime = new DateTime(2026, 9, 20, 12, 30, 0, DateTimeKind.Utc);

        public string ParentPublicProperty => "parent property";

        public Guid ReadParentPrivateGuidForTest() => ParentPrivateGuid;
    }

    public sealed class TestUPilotDumpNestedLeafObject
    {
        public string LeafName = "nested leaf";
        public int LeafNumber = 9;
    }

    public sealed class TestUPilotDumpNestedChildObject
    {
        public bool ChildEnabled = true;
        public TestUPilotDumpNestedLeafObject NestedLeaf = new TestUPilotDumpNestedLeafObject();
    }

    public sealed class TestUPilotDumpNestedObject
    {
        public int NestedInt = 7;
        public string NestedString = "nested";
        public TestUPilotDumpNestedChildObject NestedChild = new TestUPilotDumpNestedChildObject();
    }

    /// <summary>
    /// Representative object graph used by the Quick Debug window to explain
    /// what csharp_object_dump can display and which members are intentionally limited.
    /// </summary>
    public sealed class TestUPilotDumpObject : TestUPilotDumpParent
    {
        public const int ConstantValue = 9001;
        public static string StaticString = "static value";

        public bool BoolValue = true;
        public char CharValue = '中';
        public sbyte SByteValue = -8;
        public byte ByteValue = 8;
        public short Int16Value = -16;
        public ushort UInt16Value = 16;
        public int Int32Value = -32;
        public uint UInt32Value = 32;
        public long Int64Value = -64;
        public ulong UInt64Value = 64;
        public float FloatValue = 1.25f;
        public double DoubleValue = 2.5d;
        public decimal DecimalValue = 3.75m;
        public string StringValue = "UPilot 对象查看测试";
        public TestUPilotDumpEnum EnumValue = TestUPilotDumpEnum.Ready;
        public Guid GuidValue = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        public DateTime DateTimeValue = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
        public DateTimeOffset DateTimeOffsetValue = new DateTimeOffset(2026, 9, 20, 16, 0, 0, TimeSpan.FromHours(8));
        public TimeSpan TimeSpanValue = TimeSpan.FromMinutes(90);
        public Uri UriValue = new Uri("https://example.invalid/upilot");
        public Version VersionValue = new Version(1, 2, 3, 4);
        public Type TypeValue = typeof(TestUPilotDumpObject);
        public int? NullableIntValue = 42;

        public Vector2 Vector2Value = new Vector2(1f, 2f);
        public Vector3 Vector3Value = new Vector3(3f, 4f, 5f);
        public Quaternion QuaternionValue = Quaternion.Euler(10f, 20f, 30f);
        public Color ColorValue = new Color(0.1f, 0.2f, 0.3f, 1f);

        public int[] ArrayValue = { 1, 2, 3 };
        public List<string> ListValue = new List<string> { "A", "B", "C" };
        public Dictionary<string, int> DictionaryValue = new Dictionary<string, int>
        {
            ["first"] = 1,
            ["second"] = 2,
        };
        public List<int> LargeListValue = new List<int>();
        public Dictionary<string, int> LargeDictionaryValue = new Dictionary<string, int>();
        public List<string> LongTextListValue = new List<string>();
        public Dictionary<string, int> LongTextDictionaryValue = new Dictionary<string, int>();
        public TestUPilotDumpNestedObject NestedValue = new TestUPilotDumpNestedObject();
        public TestUPilotDumpGrandParent PolymorphicValue = new TestUPilotDumpParent();
        public object ObjectValue = "runtime string";
        public object NullValue;
        public TestUPilotDumpObject CircularReference;

        public IntPtr NativePointer = new IntPtr(123);
        public UIntPtr NativeUnsignedPointer = new UIntPtr(456);
        public Thread IgnoredThread = Thread.CurrentThread;
        public Action DelegateValue = () => { };

        public TestUPilotDumpObject()
        {
            for (var index = 0; index < 40; index++)
                LargeListValue.Add(index);
            for (var index = 0; index < 16; index++)
                LargeDictionaryValue.Add("large-key-" + index.ToString("D2"), index);
            LongTextListValue.Add(new string('L', 181));
            LongTextListValue.Add("tail");
            LongTextDictionaryValue.Add(new string('K', 172), 1);
            LongTextDictionaryValue.Add("tail", 2);
            CircularReference = this;
        }

        public string ChildPublicProperty => "child property";
        public int ThrowingProperty => throw new InvalidOperationException("TEST_UPILOT_DUMP_GETTER_FAILURE");
        public int this[int index] => index * 10;
        public int WriteOnlyProperty { set { } }

    }
}
