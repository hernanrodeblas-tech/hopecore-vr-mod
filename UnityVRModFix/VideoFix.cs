using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;

namespace UnityVRModFix;

// Cutscene video uses VideoPlayer.renderMode = CameraFarPlane (draws straight onto one specific
// Camera object - the game's original MainCamera, which our separate VR eye cameras aren't) or
// RenderMode.RenderTexture with no texture actually assigned (goes nowhere). Either way nothing
// reaches the VR compositor. Fix: give it our own RenderTexture and display that on a panel in
// front of the player - and keep re-checking every frame rather than fixing it once, since the
// VideoPlayer here is a persistent object reused across scenes and exactly when a scene-change
// event fires relative to our own per-frame checks isn't reliable enough to fix this only once.
internal static class VideoFix
{
    private const float DistanceMeters = 4f;
    private const float PixelsToMeters = 0.0035f;

    private class Entry
    {
        public RenderTexture Texture;
        public GameObject Panel;
    }

    private static readonly Dictionary<VideoPlayer, Entry> _entries = new();
    private static Camera _leftEyeCam;
    private static float _nextScanTime;
    private static bool _subscribedToSceneChange;

    // A video's panel is only ever shown while its VideoPlayer.isPlaying is true - but that flag
    // doesn't reliably flip back to false the moment a scene transition starts (the VideoPlayer
    // is a persistent object that can carry its "still playing" state across the transition for
    // a frame or more), so the panel from the scene you just left can hang in front of the
    // camera in the next scene. Force every panel hidden immediately on scene change; Tick()
    // will legitimately re-show it within the same frame if the new scene's video is actually
    // still playing.
    private static void EnsureSubscribed()
    {
        if (_subscribedToSceneChange)
        {
            return;
        }
        _subscribedToSceneChange = true;
        SceneManager.activeSceneChanged += (_, _) =>
        {
            foreach (var entry in _entries.Values)
            {
                if (entry.Panel != null)
                {
                    entry.Panel.SetActive(false);
                }
            }
        };
    }

    internal static void Tick()
    {
        EnsureSubscribed();

        // FindObjectsByType over every VideoPlayer is unnecessary work every single frame -
        // a few checks a second is plenty to catch a newly-appeared player or a texture/mode
        // reset, and avoids stacking extra per-frame cost on top of everything else running in
        // Update (this was likely contributing to the broader frame-rate choppiness reported
        // after this fix first shipped).
        if (Time.unscaledTime < _nextScanTime)
        {
            return;
        }
        _nextScanTime = Time.unscaledTime + 0.25f;

        var vrRig = VRModInternals.GetVrRigTransform();
        if (vrRig == null)
        {
            return;
        }
        _leftEyeCam = vrRig.Find("OpenVR_VRCamera_Left")?.GetComponent<Camera>();

        foreach (var vp in Object.FindObjectsByType<VideoPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var isCameraPlaneMode = vp.renderMode == VideoRenderMode.CameraFarPlane || vp.renderMode == VideoRenderMode.CameraNearPlane;
            var isEmptyRenderTexture = vp.renderMode == VideoRenderMode.RenderTexture && vp.targetTexture == null;
            var needsFix = isCameraPlaneMode || isEmptyRenderTexture;

            if (!_entries.TryGetValue(vp, out var entry))
            {
                if (!needsFix)
                {
                    continue;
                }
                entry = CreateEntry(vp);
                _entries[vp] = entry;
            }

            // Re-assert every frame instead of trusting a one-time fix: keeps working even if
            // something else (or a scene reload of the same persistent VideoPlayer) resets it.
            if (vp.renderMode != VideoRenderMode.RenderTexture || vp.targetTexture != entry.Texture)
            {
                vp.renderMode = VideoRenderMode.RenderTexture;
                vp.targetTexture = entry.Texture;
            }
            if (entry.Panel != null)
            {
                entry.Panel.SetActive(vp.isPlaying);
                var canvas = entry.Panel.GetComponent<Canvas>();
                if (canvas.worldCamera == null)
                {
                    canvas.worldCamera = _leftEyeCam;
                }
                if (entry.Panel.transform.parent != CanvasFix.PersistentAnchor)
                {
                    entry.Panel.transform.SetParent(CanvasFix.PersistentAnchor, worldPositionStays: false);
                }
            }
        }
    }

    private static Entry CreateEntry(VideoPlayer vp)
    {
        var width = vp.width > 0 ? (int)vp.width : 1920;
        var height = vp.height > 0 ? (int)vp.height : 1080;
        var rt = new RenderTexture(width, height, 0);
        rt.Create();

        var go = new GameObject($"UnityVRModFix_Video_{vp.name}");
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = _leftEyeCam;
        go.transform.SetParent(CanvasFix.PersistentAnchor, worldPositionStays: false);

        var rect = go.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(width, height);
        rect.localScale = new Vector3(PixelsToMeters, PixelsToMeters, PixelsToMeters);
        rect.localPosition = new Vector3(0f, 0f, DistanceMeters);
        rect.localRotation = Quaternion.identity;

        var imageGO = new GameObject("VideoImage");
        imageGO.transform.SetParent(go.transform, false);
        var rawImage = imageGO.AddComponent<RawImage>();
        rawImage.texture = rt;
        var imageRect = imageGO.GetComponent<RectTransform>();
        imageRect.anchorMin = Vector2.zero;
        imageRect.anchorMax = Vector2.one;
        imageRect.sizeDelta = Vector2.zero;

        VRModFixLog.Info($"[VideoFix] Set up VideoPlayer '{vp.name}': redirected to a {width}x{height} panel in front of the player, shown while playing.");

        return new Entry { Texture = rt, Panel = go };
    }
}
