using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;

namespace UnityVRModFix;

// UnityVRMod's stereo eye cameras only render normal scene geometry: any UI Canvas in
// "Screen Space - Overlay" or "Screen Space - Camera" mode is tied to the ORIGINAL game camera
// (or drawn straight to the backbuffer) and never reaches the VR compositor, so dialogue/menu
// text is invisible in the headset. This converts those canvases to World Space and parents
// them in front of the VR rig so the existing stereo cameras pick them up like any other object.
internal static class CanvasFix
{
    // TextMeshPro's SDF shader exposes no ZTest material property, and the "Distance Field
    // Overlay" shader variant that would ignore depth isn't included in this build (Shader.Find
    // returns null for it) - so dialogue/menu text still gets hidden behind nearby world
    // geometry. Fixed via RenderEyeOverlayFix instead: every world-space canvas we convert gets
    // moved onto this otherwise-unused layer, which that fix re-renders in a second pass per eye
    // with the depth buffer cleared first, guaranteeing it draws on top regardless of the
    // TMP/UI shader's own ZTest setting.
    internal const int OverlayLayer = 31;

    private const float DistanceMeters = 2f;
    private const float PixelsToMeters = 0.001f;

    // Full-screen effects (cutscene video playback) are meant to fill the whole view, not sit
    // as a small floating panel. Push them further back and scale them up so they fill the VR
    // field of view - and keep them behind the dialogue panel (at DistanceMeters) so text still
    // draws in front. (The retro pixelation filter canvases look the same shape but were tried
    // and reverted: that texture is rendered from the flat camera's own viewpoint, not the
    // player's actual VR gaze, so displaying it just looks like a mismatched flat recording
    // pasted in front of your face - unlike video, which is flat content anyway.)
    private const float FullScreenEffectDistanceMeters = 4f;
    private const float FullScreenEffectPixelsToMeters = 0.006f;

    // The screen-flash/fade-to-color panel (ship explosion blinding you, fades between scenes)
    // wants to feel right up against your face, not floating at video-cutscene distance - kept as
    // its own distance/scale so tuning it doesn't also move the video panel.
    private const float FadeEffectDistanceMeters = 0.6f;
    private const float FadeEffectPixelsToMeters = 0.006f;

    private static readonly HashSet<Canvas> _converted = new();
    private static readonly HashSet<Canvas> _skipped = new();
    // The same "Fade Canvas" object doubles as the end-credits panel (real readable text) as
    // well as a pure screen-flash/fade effect (no text) - so unlike other canvases, its size
    // can't be decided once at conversion time. Tracked separately so Tick() can keep
    // recomputing it every pass based on whether it currently has active text on it.
    private static readonly HashSet<Canvas> _fadeCanvases = new();
    // Scene the canvas belonged to *before* we adopted it under the DontDestroyOnLoad anchor -
    // needed so scene-change cleanup can tell "this level's dialogue canvas, now stale" apart
    // from "the crosshair, which lives on the persistent player object and is still valid".
    private static readonly Dictionary<Canvas, string> _originScene = new();

    private static bool _setGlobalZTestOverride;
    private static float _nextScanTime;
    private static Transform _persistentAnchor;

    // Shared with other fixes (e.g. VideoFix) that also want a screen-space element to follow
    // the headset's gaze - one anchor kept in sync each frame, not several independent copies.
    internal static Transform PersistentAnchor
    {
        get
        {
            EnsurePersistentAnchor();
            return _persistentAnchor;
        }
    }

    // Keeping the anchor glued to the headset's gaze - call this from LateUpdate, not Update.
    // UnityVRMod's own Update() chain is what writes the eye cameras' pose for HMD head
    // tracking (VRModBehaviour -> ... -> RenderEye); if we read that transform in our own
    // Update() we can run before it and read last frame's stale pose, costing a whole frame of
    // lag on top of however Unity happens to order things - showing up as visibly choppy
    // dialogue/menu panels. LateUpdate is guaranteed to run after every Update this frame.
    internal static void LateTick()
    {
        var vrRig = VRModInternals.GetVrRigTransform();
        if (vrRig == null)
        {
            return;
        }
        var leftEyeCam = vrRig.Find("OpenVR_VRCamera_Left")?.GetComponent<Camera>();

        EnsurePersistentAnchor();
        if (leftEyeCam != null)
        {
            _persistentAnchor.SetPositionAndRotation(
                leftEyeCam.transform.position, leftEyeCam.transform.rotation);
        }
    }

    internal static void Tick()
    {
        var vrRig = VRModInternals.GetVrRigTransform();
        if (vrRig == null)
        {
            return;
        }
        var leftEyeCam = vrRig.Find("OpenVR_VRCamera_Left")?.GetComponent<Camera>();
        EnsurePersistentAnchor();

        // Scanning every Canvas in the scene for newly-appeared ones is expensive as scenes
        // grow and doesn't need to happen every frame - a few checks a second is plenty and
        // keeps scene-change hitches from stacking on top of UnityVRMod's own rig
        // teardown/rebuild cost.
        if (Time.unscaledTime < _nextScanTime)
        {
            return;
        }
        _nextScanTime = Time.unscaledTime + 0.25f;

        if (!_setGlobalZTestOverride)
        {
            _setGlobalZTestOverride = true;
            // Unity's built-in UI shaders (Image, legacy Text) read this global property to
            // decide how they depth-test against 3D geometry - it exists specifically so World
            // Space Canvas UI can be told to always draw on top instead of getting hidden
            // behind whatever NPC/prop happens to be between the player and the panel.
            Shader.SetGlobalInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
            VRModFixLog.Info("[CanvasFix] Set global UI ZTest to Always (dialogue/menu text draws on top of world geometry).");
        }

        var followTarget = _persistentAnchor;

        var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        foreach (var canvas in canvases)
        {
            if (_skipped.Contains(canvas))
            {
                continue;
            }

            if (_converted.Contains(canvas))
            {
                // Keep it parented under the eye camera even if the game re-enables/moves it.
                if (canvas.transform.parent != followTarget)
                {
                    Reparent(canvas, followTarget);
                }
                if (canvas.worldCamera == null)
                {
                    canvas.worldCamera = leftEyeCam;
                }
                if (_fadeCanvases.Contains(canvas))
                {
                    ResizeFadeCanvas(canvas);
                }
                continue;
            }

            if (canvas.renderMode == RenderMode.WorldSpace)
            {
                continue;
            }

            // Full-screen decorative/background overlays (retro screen filters, vignette
            // frames, freeze-frame effects) carry no text and just look like a floating
            // second copy of the game if we drag them into world space. Only convert
            // canvases that actually have readable text on them - except the crosshair,
            // which has no text but is the one image-only overlay we actually need to see.
            var hasText = canvas.GetComponentsInChildren<Text>(true).Length > 0
                || canvas.GetComponentsInChildren<TMP_Text>(true).Length > 0;
            var isCrosshair = canvas.name.Contains("Crosshair");
            var isVideo = IsVideoCanvas(canvas);
            // The screen-flash/fade-to-color canvas (ship explosion blinding you, fades between
            // scenes, etc.) is meant to cover the ENTIRE view - at the normal dialogue-box
            // distance/scale it only covers a small area of the much wider VR field of view,
            // showing up as a small white rectangle hovering in front of you instead of an
            // actual blinding flash. Treat it like the other full-screen effects.
            var isFade = canvas.name.Contains("Fade");
            if (!hasText && !isCrosshair && !isVideo && !isFade)
            {
                VRModFixLog.Info($"[CanvasFix] Skipping '{canvas.name}' (no text content, likely a background/overlay effect).");
                _skipped.Add(canvas);
                continue;
            }

            _originScene[canvas] = canvas.gameObject.scene.name;
            ConvertToWorldSpace(canvas, followTarget, leftEyeCam, isVideo, isFade);
            _converted.Add(canvas);
            if (isFade)
            {
                _fadeCanvases.Add(canvas);
            }
        }
    }

    private static bool IsVideoCanvas(Canvas canvas)
    {
        var rawImages = canvas.GetComponentsInChildren<RawImage>(true);
        if (rawImages.Length == 0)
        {
            return false;
        }
        var videoTextures = UnityEngine.Object.FindObjectsByType<VideoPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Select(vp => vp.targetTexture)
            .Where(tex => tex != null)
            .ToList();
        return rawImages.Any(img => img.texture != null && videoTextures.Contains(img.texture));
    }

    private static void ConvertToWorldSpace(Canvas canvas, Transform followTarget, Camera leftEyeCam, bool isVideo, bool isFade)
    {
        var rect = canvas.GetComponent<RectTransform>();
        var size = rect.sizeDelta;
        if (size.x <= 0f || size.y <= 0f)
        {
            size = new Vector2(1920f, 1080f);
        }

        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = leftEyeCam;

        Reparent(canvas, followTarget);

        var fullScreen = isVideo || isFade;
        var pixelsToMeters = isFade ? FadeEffectPixelsToMeters : fullScreen ? FullScreenEffectPixelsToMeters : PixelsToMeters;
        var distance = isFade ? FadeEffectDistanceMeters : fullScreen ? FullScreenEffectDistanceMeters : DistanceMeters;
        rect.sizeDelta = size;
        rect.localScale = new Vector3(pixelsToMeters, pixelsToMeters, pixelsToMeters);
        rect.localPosition = new Vector3(0f, 0f, distance);
        rect.localRotation = Quaternion.identity;

        // TextMeshPro uses its own SDF shader with a separate "_ZTest" material property,
        // not the legacy UI shaders' unity_GUIZTestMode - has to be set per instance.
        foreach (var tmpText in canvas.GetComponentsInChildren<TMP_Text>(true))
        {
            var mat = tmpText.fontMaterial;
            var overlayShader = Shader.Find("TextMeshPro/Distance Field Overlay");
            if (overlayShader != null)
            {
                mat.shader = overlayShader;
                VRModFixLog.Info($"[CanvasFix] Switched '{tmpText.name}' to Distance Field Overlay shader (ZTest ignored).");
            }
            else
            {
                foreach (var propName in new[] { "_ZTestMode", "_ZTest", "unity_GUIZTestMode" })
                {
                    if (mat.HasProperty(propName))
                    {
                        mat.SetFloat(propName, (float)UnityEngine.Rendering.CompareFunction.Always);
                    }
                }
            }
        }

        // Non-text Graphics (e.g. background Images) still use the legacy UI shader, which
        // reads _ZTestMode too — but per-material via SetFloat, not the global property in the
        // legacy "UI/Default" shader.
        foreach (var graphic in canvas.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
        {
            if (graphic is TMP_Text)
            {
                continue;
            }
            if (graphic.material != null && graphic.material.HasProperty("_ZTestMode"))
            {
                graphic.material.SetFloat("_ZTestMode", (float)UnityEngine.Rendering.CompareFunction.Always);
            }
        }

        // Some menus (e.g. the main menu) mix real button text with a RawImage showing a
        // *live* RenderTexture - a separate camera continuously re-rendering the flat screen for
        // a retro pixelation effect. Pulling the whole canvas into world space in front of the
        // VR camera means that background camera can end up seeing this very panel floating in
        // the world and capturing it into the same texture it's displaying - a recursive
        // "screen showing itself" effect the user's been seeing since the very first VR test.
        // The button text is worth keeping visible; the live background feed isn't - disable it.
        if (!fullScreen)
        {
            foreach (var rawImage in canvas.GetComponentsInChildren<RawImage>(true))
            {
                if (rawImage.texture is RenderTexture)
                {
                    rawImage.enabled = false;
                    VRModFixLog.Info($"[CanvasFix] Disabled live-camera-feed RawImage '{rawImage.name}' on '{canvas.name}' (would recursively capture its own world-space panel).");
                }
            }
        }

        // Only text-bearing canvases (dialogue/menus) go on the overlay layer - that's the
        // actual reported bug (text hidden behind geometry). The crosshair and video panel
        // already worked fine going through the normal single render pass, and putting them
        // through the depth-cleared overlay pass too caused a full-screen yellow wash - most
        // likely one of their Graphics has a background rect that isn't meant to render opaque
        // in isolation. Keep this fix scoped to what's actually broken.
        var hasTmpText = canvas.GetComponentsInChildren<TMP_Text>(true).Length > 0;
        if (hasTmpText)
        {
            SetLayerRecursively(canvas.transform, OverlayLayer);
        }

        VRModFixLog.Info(
            $"[CanvasFix] Converted '{canvas.name}' ({size.x}x{size.y}px) from " +
            $"{canvas.renderMode} to World Space, parented under VR rig.");
    }

    // Re-checked every Tick() pass: when the Fade Canvas currently has active readable text
    // (end credits), keep it at normal dialogue-panel size/distance so it stays legible instead
    // of blowing up to giant letters. Otherwise (a pure flash/fade, no text active), use the
    // big up-close size so it actually covers the field of view like a real blinding flash.
    private static void ResizeFadeCanvas(Canvas canvas)
    {
        var hasActiveText = false;
        foreach (var t in canvas.GetComponentsInChildren<TMP_Text>(true))
        {
            if (t.gameObject.activeInHierarchy) { hasActiveText = true; break; }
        }
        if (!hasActiveText)
        {
            foreach (var t in canvas.GetComponentsInChildren<Text>(true))
            {
                if (t.gameObject.activeInHierarchy) { hasActiveText = true; break; }
            }
        }

        var pixelsToMeters = hasActiveText ? PixelsToMeters : FadeEffectPixelsToMeters;
        var distance = hasActiveText ? DistanceMeters : FadeEffectDistanceMeters;

        var rect = canvas.GetComponent<RectTransform>();
        rect.localScale = new Vector3(pixelsToMeters, pixelsToMeters, pixelsToMeters);
        rect.localPosition = new Vector3(0f, 0f, distance);
    }

    private static void SetLayerRecursively(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        foreach (Transform child in root)
        {
            SetLayerRecursively(child, layer);
        }
    }

    private static void EnsurePersistentAnchor()
    {
        if (_persistentAnchor != null)
        {
            return;
        }

        // The VR rig (and its eye cameras) lives in whatever scene is currently active and gets
        // fully destroyed and rebuilt on every scene change (UnityVRMod's Automatic Safe Mode).
        // Parenting a Canvas directly under the eye camera means it gets migrated into that
        // scene and destroyed along with the rig on the next scene change - permanently, since
        // the original object (e.g. the game's own Dialogue Canvas) is gone, not just detached.
        // Instead, keep a separate anchor object marked DontDestroyOnLoad, park canvases under
        // that, and just copy the eye camera's pose onto it every tick.
        var anchorGO = new GameObject("UnityVRModFix_UIAnchor");
        UnityEngine.Object.DontDestroyOnLoad(anchorGO);
        _persistentAnchor = anchorGO.transform;

        // Adopting a canvas under the DontDestroyOnLoad anchor also exempts it from being
        // destroyed when ITS OWN originating scene unloads - which used to happen normally and
        // is why each scene gets its own fresh Dialogue/menu canvases. Without that, every
        // scene's canvases just pile up on top of each other forever. So on every scene change,
        // destroy whichever adopted canvases came from a scene that isn't Persistent (i.e.
        // belonged to the level just left) and let the new scene's own fresh canvases get
        // discovered and converted normally. Canvases that came from Persistent (like the
        // player's crosshair) are left alone - they're still valid.
        SceneManager.activeSceneChanged += (_, _) =>
        {
            foreach (var canvas in _converted)
            {
                if (canvas == null || !_originScene.TryGetValue(canvas, out var scene) || scene == "Persistent")
                {
                    continue;
                }
                UnityEngine.Object.Destroy(canvas.gameObject);
            }
            _converted.Clear();
            _skipped.Clear();
            _originScene.Clear();
            _fadeCanvases.Clear();
            VRModFixLog.Info("[CanvasFix] Scene changed: cleared this level's adopted canvases so the new scene's own UI gets picked up fresh.");
        };
    }

    private static void Reparent(Canvas canvas, Transform followTarget)
    {
        canvas.transform.SetParent(followTarget, worldPositionStays: false);
    }
}

internal static class VRModFixLog
{
    internal static Action<object> LogInfo;

    internal static void Info(object message) => LogInfo?.Invoke(message);
}
