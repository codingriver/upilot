// -----------------------------------------------------------------------
// UPilot Editor - https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------
// Unity 6+: Object.GetInstanceID is obsolete; use EntityId wire IDs.
// EntityIdToObject is used when present, with a scan fallback for earlier Unity 6 builds.
// Unity 2022 LTS: int instance IDs.

using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public static class UPilotEntityIds
    {
#if UNITY_6000_0_OR_NEWER
        public static ulong ToWireId(Object o)
        {
            var id = o != null ? EntityId.ToULong(o.GetEntityId()) : 0UL;
            Logger.Log("EntityIds", $"ToWireId: {o?.name} -> {id}");
            return id;
        }

        public static GameObject GameObjectFromWireId(ulong wireId)
        {
            if (wireId == 0UL)
            {
                return null;
            }

            var entityId = EntityId.FromULong(wireId);
            var entityIdToObject = typeof(EditorUtility).GetMethod("EntityIdToObject", new[] { typeof(EntityId) });
            var go = entityIdToObject != null
                ? entityIdToObject.Invoke(null, new object[] { entityId }) as GameObject
                : null;
            if (go == null)
            {
                foreach (var candidate in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (candidate != null && ToWireId(candidate) == wireId)
                    {
                        go = candidate;
                        break;
                    }
                }
            }
            Logger.Log("EntityIds", $"GameObjectFromWireId: {wireId} -> {go?.name}");
            return go;
        }
#else
        public static ulong ToWireId(Object o)
        {
            var id = o != null ? (ulong)(uint)o.GetInstanceID() : 0UL;
            Logger.Log("EntityIds", $"ToWireId: {o?.name} -> {id}");
            return id;
        }

        public static GameObject GameObjectFromWireId(ulong wireId)
        {
            if (wireId == 0UL)
            {
                return null;
            }

            var go = EditorUtility.InstanceIDToObject(unchecked((int)(uint)wireId)) as GameObject;
            Logger.Log("EntityIds", $"GameObjectFromWireId: {wireId} -> {go?.name}");
            return go;
        }
#endif
    }
}
