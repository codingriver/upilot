using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    // Keep this MonoBehaviour in its own matching script file so Unity can
    // persist and restore the component in Prefab assets on every target version.
    public sealed class PrefabReferenceLiteralProbe : MonoBehaviour
    {
        public string literal;
    }
}
