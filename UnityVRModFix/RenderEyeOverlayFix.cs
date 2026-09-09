using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace UnityVRModFix;

// TextMeshPro's SDF shader exposes no ZTest material property, so dialogue/menu text still gets
// hidden behind nearby world geometry (CanvasFix's global unity_GUIZTestMode override only helps
// legacy UI shaders). Fixing this properly means drawing our world-space UI in a pass that
// ignores depth - the classic "second camera, depth cleared, renders after everything else"
// trick used for first-person weapon models in shooters.
//
// UnityVRMod's eye cameras don't use Unity's automatic camera-stacking though: they're disabled
// (so Unity's own per-frame render loop skips them) and UnityVRMod calls Camera.Render() on them
// manually once per eye, then immediately Submits that eye's texture to the OpenVR compositor -
// all inside one method. An earlier attempt hooked *after* that whole method and Submitted a
// second time to add our overlay pass - that froze the VR view on every scene change (submitting
// the same eye twice per frame isn't something the OpenVR compositor is meant to tolerate).
//
// This version never touches Submit at all. Camera.Render() itself is a public, generic engine
// method - by postfixing it directly, we get to run additional code for a specific camera
// exactly when *that* Render() call returns, while its targetTexture is still pointed at the eye
// texture (UnityVRMod only clears targetTexture *after* Render() returns, right before its own
// single Submit call). So: after the original per-eye Render() finishes, quickly re-render just
// our UI layer into that same still-assigned texture with depth cleared, then hand control back -
// UnityVRMod's own untouched code submits the combined result exactly once, like it always did.
internal static class RenderEyeOverlayFix
{
    private static bool _inOverlayPass;

    internal static void Apply(Harmony harmony)
    {
        var renderMethod = typeof(Camera).GetMethod("Render", System.Type.EmptyTypes);
        if (renderMethod == null)
        {
            VRModFixLog.Info("[RenderEyeOverlayFix] Could not find Camera.Render(); skipping patch.");
            return;
        }

        harmony.Patch(renderMethod, postfix: new HarmonyMethod(
            typeof(RenderEyeOverlayFix).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static)));

        VRModFixLog.Info("[RenderEyeOverlayFix] Patched Camera.Render to redraw world-space UI on top of the VR eye cameras with depth cleared.");
    }

    // Fires after EVERY Camera.Render() call in the whole game - bail out immediately unless
    // it's one of our two eye cameras actively mid-render-for-VR (targetTexture only points at
    // the eye texture during UnityVRMod's own manual render window).
    private static void Postfix(Camera __instance)
    {
        if (_inOverlayPass || __instance == null)
        {
            return;
        }
        if (__instance.name != "OpenVR_VRCamera_Left" && __instance.name != "OpenVR_VRCamera_Right")
        {
            return;
        }
        var tex = __instance.targetTexture;
        if (tex == null)
        {
            return;
        }

        _inOverlayPass = true;
        var savedClearFlags = __instance.clearFlags;
        var savedCullingMask = __instance.cullingMask;
        var invertCulling = GL.invertCulling;
        var savedActiveRt = RenderTexture.active;
        try
        {
            // CameraClearFlags.Depth turned the whole view solid yellow on this manually-driven,
            // permanently-disabled camera - almost certainly Unity's default procedural skybox
            // getting drawn anyway (its default sun/atmosphere settings render as a yellow-orange
            // gradient) despite asking for a depth-only clear. Side-step that entirely: clear just
            // the depth buffer ourselves with GL.Clear, and tell the camera not to clear anything
            // at all, so there's no clear-flags codepath left that could decide to draw a skybox.
            RenderTexture.active = tex;
            GL.Clear(true, false, Color.clear);
            RenderTexture.active = savedActiveRt;

            __instance.clearFlags = CameraClearFlags.Nothing;
            __instance.cullingMask = 1 << CanvasFix.OverlayLayer;
            GL.invertCulling = true;
            __instance.Render();
        }
        catch (System.Exception ex)
        {
            // If the overlay render itself throws, the finally below still restores the eye
            // camera's real settings - without it, a failure here would leave the main eye
            // camera permanently stuck rendering only our UI layer with depth never cleared
            // (i.e. the whole world disappears, replaced by whatever garbage is left in the
            // never-cleared color buffer).
            VRModFixLog.Info($"[RenderEyeOverlayFix] Overlay render failed: {ex}");
        }
        finally
        {
            GL.invertCulling = invertCulling;
            __instance.clearFlags = savedClearFlags;
            __instance.cullingMask = savedCullingMask;
            _inOverlayPass = false;
        }
    }
}
