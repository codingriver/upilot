// Runtime-capable fixture for persisted prefab tests; excluded without test assemblies.
using System;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotPrefabPatchProbe : MonoBehaviour
    {
        public enum Mode { First = 3, Second = 17 }
        [Serializable] public class Values { public Mode mode; public Mode adjacent = Mode.Second; public int amount = 5; }
        public Values nested = new();
        public int[] values = { 1, 2, 3 };

        private void OnValidate()
        {
            if (nested != null && nested.amount == 99) nested.amount = 98;
        }
    }
}
