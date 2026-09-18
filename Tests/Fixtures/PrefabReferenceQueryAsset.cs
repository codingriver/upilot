using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    // Kept in a matching script asset so temporary ScriptableObject fixtures
    // retain a stable MonoScript identity across supported Unity versions.
    public sealed class PrefabReferenceQueryAsset : ScriptableObject
    {
        public UnityEngine.Object objectTarget;
    }
}
