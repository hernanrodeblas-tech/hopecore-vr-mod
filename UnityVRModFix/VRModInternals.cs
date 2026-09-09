using System;
using System.Reflection;
using UnityEngine;

namespace UnityVRModFix;

// Reflection helpers into UnityVRMod's private state. UnityVRMod doesn't expose its VR rig,
// tracked camera, or camera-setup instance publicly, so we reach in the same way for every
// feature that needs them instead of duplicating the lookup chain everywhere.
internal static class VRModInternals
{
    private static Type _coreType;
    private static PropertyInfo _vrVisualizationFeatureProp;
    private static FieldInfo _cameraSetupField;
    private static FieldInfo _vrRigField;
    private static FieldInfo _trackedCameraGoField;

    internal static object GetCameraSetup()
    {
        _coreType ??= Type.GetType("UnityVRMod.Core.VRModCore, UnityVRMod");
        if (_coreType == null)
        {
            return null;
        }

        _vrVisualizationFeatureProp ??= _coreType.GetProperty(
            "VrVisualizationFeature", BindingFlags.NonPublic | BindingFlags.Static);
        var visFeature = _vrVisualizationFeatureProp?.GetValue(null);
        if (visFeature == null)
        {
            return null;
        }

        _cameraSetupField ??= visFeature.GetType().GetField(
            "_cameraSetup", BindingFlags.NonPublic | BindingFlags.Instance);
        return _cameraSetupField?.GetValue(visFeature);
    }

    internal static Transform GetVrRigTransform(object cameraSetup = null)
    {
        cameraSetup ??= GetCameraSetup();
        if (cameraSetup == null)
        {
            return null;
        }

        _vrRigField ??= cameraSetup.GetType().GetField(
            "_vrRig", BindingFlags.NonPublic | BindingFlags.Instance);
        var vrRigGO = _vrRigField?.GetValue(cameraSetup) as GameObject;
        return vrRigGO?.transform;
    }

    internal static Transform GetTrackedOriginalCameraTransform(object cameraSetup = null)
    {
        cameraSetup ??= GetCameraSetup();
        if (cameraSetup == null)
        {
            return null;
        }

        _trackedCameraGoField ??= cameraSetup.GetType().GetField(
            "_currentlyTrackedOriginalCameraGO", BindingFlags.NonPublic | BindingFlags.Instance);
        var go = _trackedCameraGoField?.GetValue(cameraSetup) as GameObject;
        return go?.transform;
    }
}
