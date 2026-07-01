// -----------------------------------------------------------------------
// Upilot Editor - https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------
// Unity 6.3+: Object.GetInstanceID / EditorUtility.InstanceIDToObject are
// obsolete; use EntityId + ToULong / EntityIdToObject.
// Unity 6.0-6.2 and Unity 2022 LTS: int instance IDs.

using UnityEditor;
using UnityEngine;

namespace codingriver.upilot
{
    public static class UpilotEntityIds
    {
#if UNITY_6000_3_OR_NEWER
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

            var go = EditorUtility.EntityIdToObject(EntityId.FromULong(wireId)) as GameObject;
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
