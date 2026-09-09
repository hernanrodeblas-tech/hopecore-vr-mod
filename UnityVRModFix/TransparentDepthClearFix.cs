using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityVRModFix;

// Third attempt at drawing dialogue/menu text on top of nearby world geometry (TextMeshPro's SDF
// shader exposes no ZTest property, and the "Distance Field Overlay" variant that would ignore
// depth isn't included in this build). The first two attempts both re-rendered an eye camera a
// SECOND time within the same frame - once by re-Submitting to the OpenVR compositor (froze the
// VR view on every scene change), once via a second plain Camera.Render() call (turned the whole
// view solid yellow, regardless of which clear-flags/culling details were used). Both point at
// that extra manual Render() call itself being incompatible with this pipeline, not a detail of
// how it was done.
//
// This attempt never calls Render() a second time. A CommandBuffer attached to a camera via
// AddCommandBuffer runs as EXTRA COMMANDS inside that camera's own single, normal Render() call -
// so we just ask Unity to clear the depth buffer right before it draws any transparent-queue
// geometry (world-space UI included) as part of the SAME render pass UnityVRMod already does once
// per eye. Trade-off: this clears depth for every transparent object that frame, not just our UI,
// so any other transparent VFX would also stop being occluded by opaque geometry behind it for
// that one frame - worth watching for in VFX-heavy scenes, but a much smaller risk than an extra
// full render pass.
internal static class TransparentDepthClearFix
{
    internal static void Apply(Harmony harmony)
    {
        var setupType = Type.GetType(
            "UnityVRMod.Features.VrVisualization.VrCameraSetup_CoreOpenVR, UnityVRMod");
        var setupRig = setupType?.GetMethod("SetupCameraRig", BindingFlags.Public | BindingFlags.Instance);
        if (setupRig == null)
        {
            VRModFixLog.Info("[TransparentDepthClearFix] Could not find SetupCameraRig; skipping patch.");
            return;
        }

        harmony.Patch(setupRig, postfix: new HarmonyMethod(
            typeof(TransparentDepthClearFix).GetMethod(nameof(OnRigSetup), BindingFlags.NonPublic | BindingFlags.Static)));

        VRModFixLog.Info("[TransparentDepthClearFix] Patched VR rig setup to clear depth before transparent/UI geometry each eye render.");
    }

    // The eye cameras get destroyed and recreated on every scene change (and every Automatic Safe
    // Mode toggle), so the command buffer has to be re-attached every time SetupCameraRig runs -
    // it doesn't survive being attached once.
    private static void OnRigSetup()
    {
        var vrRig = VRModInternals.GetVrRigTransform();
        if (vrRig == null)
        {
            return;
        }

        AttachTo(vrRig.Find("OpenVR_VRCamera_Left")?.GetComponent<Camera>());
        AttachTo(vrRig.Find("OpenVR_VRCamera_Right")?.GetComponent<Camera>());
    }

    private static void AttachTo(Camera cam)
    {
        if (cam == null)
        {
            return;
        }

        var cmd = new CommandBuffer { name = "UnityVRModFix_ClearDepthBeforeTransparent" };
        // DIAGNOSTIC: also clearing color to a loud magenta here temporarily, just to see
        // whether this command buffer executes at all on a manually-Render()'d, disabled camera.
        cmd.ClearRenderTarget(true, true, Color.magenta);
        cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, cmd);
    }
}
