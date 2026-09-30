// Runtime-capable fixture for persisted prefab tests; excluded without test assemblies.
using System;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public abstract class UPilotPrefabPatchProbeBase : MonoBehaviour
    {
        [SerializeField] private UPilotPrefabPatchProbe.Mode m_inheritedMode = UPilotPrefabPatchProbe.Mode.First;
        [SerializeField] private float m_inheritedSpeed = 1.25f;
        public UPilotPrefabPatchProbe.Mode InheritedMode => m_inheritedMode;
        public float InheritedSpeed => m_inheritedSpeed;
    }

    public sealed class UPilotPrefabPatchProbe : UPilotPrefabPatchProbeBase
    {
        public enum Mode { First = 3, Second = 17 }
        [Serializable] public class Values { public Mode mode; public Mode adjacent = Mode.Second; public int amount = 5; }
        [SerializeField] private Mode m_directMode = Mode.First;
        [SerializeField] private float m_directSpeed = 2.5f;
        public Mode DirectMode => m_directMode;
        public float DirectSpeed => m_directSpeed;
        public Values nested = new();
        public int[] values = { 1, 2, 3 };

        private void OnValidate()
        {
            if (nested != null && nested.amount == 99) nested.amount = 98;
        }
    }
}
