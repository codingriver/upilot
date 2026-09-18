// -----------------------------------------------------------------------
// UPilot Editor tests
// -----------------------------------------------------------------------

using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    // Kept in the Editor test assembly so the containment test loads a real
    // ExecuteAlways component. The service's opt-in loaded-boundary seam,
    // rather than Unity's version-dependent component IsDirty flag, supplies
    // the deterministic changed-state signal.
    [ExecuteAlways]
    public sealed class UPilotPrefabReferenceDirtyLoadProbe : MonoBehaviour
    {
    }
}
