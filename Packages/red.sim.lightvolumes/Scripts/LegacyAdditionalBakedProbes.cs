#if UNITY_EDITOR
using System;
using System.Reflection;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace VRCLightVolumes {
    // Reflection shim for UnityEditor.Experimental.Lightmapping APIs that were marked
    // [Obsolete(error: true)] (and in some cases removed) starting in Unity 6.4. Direct
    // calls fail to compile under that obsoletion; reflection bypasses the compile-time
    // check. On editors where the API has been physically removed, calls become no-ops
    // and GetAdditionalBakedProbes returns false so callers degrade gracefully.
    internal static class LegacyAdditionalBakedProbes {

        private static readonly Type LightmappingType =
            Type.GetType("UnityEditor.Experimental.Lightmapping, UnityEditor.CoreModule") ??
            Type.GetType("UnityEditor.Experimental.Lightmapping, UnityEditor");

        private static readonly MethodInfo SetMethod = LightmappingType?.GetMethod(
            "SetAdditionalBakedProbes",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(int), typeof(Vector3[]) },
            null);

        private static readonly MethodInfo GetMethod = LightmappingType?.GetMethod(
            "GetAdditionalBakedProbes",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(int), typeof(NativeArray<SphericalHarmonicsL2>), typeof(NativeArray<float>) },
            null);

        private static readonly EventInfo CompletedEvent = LightmappingType?.GetEvent(
            "additionalBakedProbesCompleted",
            BindingFlags.Public | BindingFlags.Static);

        public static void SetAdditionalBakedProbes(int id, Vector3[] positions) {
            SetMethod?.Invoke(null, new object[] { id, positions });
        }

        public static bool GetAdditionalBakedProbes(int id, NativeArray<SphericalHarmonicsL2> probes, NativeArray<float> validity) {
            if (GetMethod == null) return false;
            return (bool)GetMethod.Invoke(null, new object[] { id, probes, validity });
        }

        public static void AddAdditionalBakedProbesCompleted(Action handler) {
            CompletedEvent?.GetAddMethod(true)?.Invoke(null, new object[] { handler });
        }

        public static void RemoveAdditionalBakedProbesCompleted(Action handler) {
            CompletedEvent?.GetRemoveMethod(true)?.Invoke(null, new object[] { handler });
        }
    }
}
#endif
