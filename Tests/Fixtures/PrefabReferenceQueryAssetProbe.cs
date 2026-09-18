using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    // Matching file/class name keeps the component attachable and serializable
    // in the transient missing-stable-identity fixture.
    public sealed class PrefabReferenceQueryAssetProbe : MonoBehaviour
    {
        public UnityEngine.Object target;
    }
}
