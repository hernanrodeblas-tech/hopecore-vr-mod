# VR Mod for HOPECORE / "Control, I'm Not Coming Back"

*[Versión en español](README.es.md)*

## AI usage disclaimer

This mod was developed with the help of Claude (Claude Code, Anthropic), an LLM-based coding assistant.
Claude wrote most of the `UnityVRModFix` code, decompiled and analyzed the game's and UnityVRMod's code
to diagnose issues, and drafted this README, all under the direction and supervision of a human (every
design decision, in-headset test, and final validation was done by a person). As with any AI-generated
code: review before trusting blindly, especially if you plan to modify or reuse it in another project.

6DOF VR mod (head tracking only, no motion controllers) for this non-VR Unity game, built as a personal/
fun project. Movement and every action still use keyboard/mouse as usual; the head drives the camera and
a center-screen gaze pointer is used to interact. Status: played start to finish in VR with no issues
from our own code (a couple of known limitations remain, see below).

## Installation

### Option A: all-in-one package (recommended)

Grab **`HOPECORE-VR-Mod-vX.Y-AllInOne.zip`** from the [Releases page](../../releases). It bundles
BepInEx, UnityVRMod, and our own `UnityVRModFix` plugin together, already configured.

1. Extract the zip's contents directly into the game's install folder (the one with `HOPECORE.exe`).
2. Steam: right-click the game -> Properties -> Launch Options -> add `-force-d3d11`.
3. Make sure SteamVR is installed and your headset is connected/on.
4. Launch the game normally from Steam.

(Full steps also included as `INSTALL.txt` inside the zip.)

### Option B: install everything separately

If you'd rather assemble it yourself (e.g. to use a different BepInEx/UnityVRMod version):

1. Download and extract [BepInEx 6](https://github.com/BepInEx/BepInEx) (Mono variant) into the game's
   root folder.
2. Download and extract [UnityVRMod](https://github.com/NewUnityModder/UnityVRMod) (OpenVR + Mono
   variant) into `BepInEx\plugins\UnityVRMod\`.
3. Grab just `UnityVRModFix.dll` from this repo's Releases and put it in
   `BepInEx\plugins\UnityVRModFix\UnityVRModFix.dll` (or build it yourself, see "Building and deploying"
   below).
4. Same last two steps as Option A: `-force-d3d11` launch option, SteamVR running before you launch.

## Licenses / third-party software

This repo (`UnityVRModFix`'s source code) is our own; see the git history for authorship. It depends on
and, in the all-in-one release package, bundles two separate third-party projects, unmodified:

- **[BepInEx](https://github.com/BepInEx/BepInEx)** 6 (bleeding-edge #785, Mono variant); GNU Lesser
  General Public License v2.1. License text included as `LICENSE-BepInEx.txt` in the release package.
- **[UnityVRMod](https://github.com/NewUnityModder/UnityVRMod)** v0.1.0-beta (OpenVR + Mono variant);
  GNU General Public License v3.0. License text included as `LICENSE-UnityVRMod.txt` in the release
  package.

Neither this repo nor the release packages contain any code or assets from the game itself.

## How it all works (architecture)

Three pieces, each in its own folder/DLL:

1. **BepInEx 6** (bleeding-edge #785, Mono variant); Unity's "mod loader". Injected via doorstop
   (`winhttp.dll` + `doorstop_config.ini`) in the game's root folder.
2. **UnityVRMod v0.1.0-beta** (`BepInEx\plugins\UnityVRMod\`); the third-party mod
   (https://github.com/NewUnityModder/UnityVRMod) that actually talks to SteamVR/OpenVR: builds the
   stereo camera rig, reads the headset's poses, and submits frames to the compositor. It knows nothing
   about this specific game.
3. **UnityVRModFix** (`mod\UnityVRModFix\`, this project); our own BepInEx plugin, written for this mod.
   Uses Harmony to patch both UnityVRMod and the game's own code (`Assembly-CSharp.dll`) to fix
   everything that doesn't work out of the box: camera not following the game, UI canvases invisible in
   VR, doubled height, videos not showing, etc. This is the only code we wrote ourselves; everything else
   is third-party.

`UnityVRModFix.dll` is built with `dotnet build -c Release` inside `mod\UnityVRModFix\` and copied by
hand to `BepInEx\plugins\UnityVRModFix\UnityVRModFix.dll`. The `.csproj` references local copies of the
game's/Unity's/UnityVRMod's DLLs (all with `<Private>false</Private>`, only to compile against their
types; never redistributed).

**UnityVRMod doesn't support D3D12** (D3D11 only), and this game boots into D3D12 by default. You have to
force `-force-d3d11` as a launch option (Steam right-click the game -> Properties -> Launch Options).

## The fixes (`mod\UnityVRModFix\*.cs`)

All applied via Harmony from `Plugin.cs` at startup. Currently active:

- **`CameraFollowFix.cs`**; UnityVRMod only copies the game camera's position/rotation onto the VR rig
  ONCE, when the rig is built. This game moves its camera with Cinemachine constantly (following the
  player, reframing shots, etc.), so without this the rig is left floating wherever the camera happened
  to be at instant zero. Re-syncs the rig's position/yaw every frame, before applying head tracking on
  top.
- **`HeightFix.cs`**; UnityVRMod uses "Standing" tracking space (the headset's real absolute height above
  the floor), which was being added on top of the game camera's already-correct eye height, doubling it.
  Switched to "Seated" (height relative to wherever the headset was when VR was activated).
- **`PointerFix.cs`**; the game's interaction raycast (`CheckRay`) used the flat camera, whose pitch
  doesn't follow the headset (only yaw does, via `CameraFollowFix`). Replaced with a raycast from the
  headset's actual gaze, so the crosshair/interaction follow wherever you look with your head.
- **`CameraCleanerFix.cs`**; a "CameraCleaner" camera (likely a sphere hiding the background, meant to
  always stay centered on the flat camera) became visible from the inside when moving your head in VR.
  Disabled while the VR rig is active.
- **`PlayerCapsuleFix.cs`**; the player itself had a visible collision-capsule mesh (checkerboard texture,
  clearly a dev placeholder), invisible in flat mode because the camera always sits exactly at its
  center. Disables its renderer while the VR rig is active.
- **`BackwardMovementFix.cs`**; two changes to movement (`FirstPersonController.FixedUpdate`): (1) the
  game blocked walking backward, removed; (2) movement is now relative to where the HEAD (headset) is
  looking, not where the body/flat camera points; so "forward" on the stick is intuitive in VR.
  Reversible via `BackwardMovementFix.Enabled = false`.
- **`GamepadEmulator.cs`** (class `ActionEnableFix`; the filename is outdated); several of the game's
  Input Actions ("Move", "Look", "Interact") start out disabled for reasons unrelated to any controller/
  device; re-enables them every frame, respecting the game's real movement locks (`playerCanMove`/
  `cameraCanMove`) and an explicit list of scenes where movement is deliberately locked (today just
  `1_ModuloMandosCohete`, the rocket control console). Also resets the `GameManager.IsInDialogue()` flag
  on scene change and startup, since it gets stuck `true` (a bug in the game itself, not ours) and would
  permanently block "Interact" if not corrected.
- **`CanvasFix.cs`**; the biggest fix. "Screen Space" canvases (the vast majority of the game's UI:
  dialogue, menus, crosshair) never reach the VR stereo cameras, so they're invisible with the headset
  on. Converts them to "World Space" and hangs them in front of the head:
  - Only converts canvases with actual text, the crosshair, video ones, or the "Fade Canvas" (color fade
    / credits); everything else is a decorative overlay (filters, resolution frames) that would just show
    up as a floating second screen, and gets skipped.
  - Hung off a `DontDestroyOnLoad` anchor (not the VR rig, which gets destroyed and rebuilt on every scene
    change) that copies the left eye camera's pose every frame; split into `Tick()` (scan for new
    canvases, throttled to 0.25s) and `LateTick()` (just move the anchor, every frame, so dialogue doesn't
    feel choppy).
  - Normal dialogue/menu panels sit 2m away; "fullScreen" ones (video) sit 4m away and much bigger.
  - The "Fade Canvas" is a special case: the same object doubles as both the ship-explosion white flash
    (no text) and the end credits (real text); its size is recomputed every `Tick()` based on whether it
    currently has active text: small/legible with text (credits), big and very close (0.6m) without text
    (a flash meant to blind/cover the whole field of view).
  - On scene change, destroys adopted canvases that didn't come from the "Persistent" scene (so they
    don't pile up forever), leaving the ones that did (like the crosshair) alone.
  - Also disables any `RawImage` showing a live `RenderTexture` (a background camera feed) inside a
    normal canvas; the main menu has a retro pixelation filter like this, and converting its canvas to
    world space made that background camera end up recording itself, a recursive "screen inside screen"
    effect.
- **`VideoFix.cs`**; `VideoPlayer`s in `CameraFarPlane`/`CameraNearPlane` mode (draw onto the original
  flat camera) never reach the VR cameras. Redirects each one to its own `RenderTexture` and a dedicated
  panel in front of the player, shown only while `isPlaying`. Reasserted every 0.25s (not every frame) and
  force-hidden on every scene change, so a video that already ended doesn't stay floating in the next
  scene.
- **`RigidbodyInterpolationFix.cs`**; objects driven by real physics (the canoe, the sliding stones) move
  in `FixedUpdate` at a fixed 50Hz; without interpolation, their visual position visibly steps at VR's
  90Hz+. Enables `Rigidbody.interpolation = Interpolate` on every non-kinematic Rigidbody in the scene,
  **except the player's own** (which moves with the same physics pattern, and enabling interpolation on
  it made the WHOLE game feel choppy, not just the canoe).
- **`CinemachineUpdateModeFix.cs`**; Rigidbody interpolation alone wasn't enough: `CinemachineBrain` (the
  game's camera) defaults to `SmartUpdate`, which for a Rigidbody target automatically chooses to update
  in `FixedUpdate` too; meaning the camera itself only changes position 50 times/sec, interpolation or
  not on the object it follows. Forces `UpdateMethod = LateUpdate` (re-evaluated every render frame),
  which combined with the interpolation above does produce smooth camera motion following a physics
  object.

## Things we tried that did NOT work (left disabled on purpose)

Dialogue/menu text getting hidden behind nearby geometry (TextMeshPro exposes no ZTest property, and the
"Distance Field Overlay" shader that would ignore it isn't included in this build). We tried 4 approaches,
all failed, code left in the repo commented out/inert in case it's revisited:

- **`RenderEyeOverlayFix.cs`**; re-render each eye camera a second time (just the UI layer, with the depth
  buffer cleared) to draw the text always on top. Tried 3 variants (double-submitting to the OpenVR
  compositor, `ClearFlags.Depth`, manual `GL.Clear` + `ClearFlags.Nothing`): the first froze the VR view
  on every scene change; the other two left the whole screen solid yellow. UnityVRMod's eye cameras are
  disabled and rendered by hand via `Camera.Render()` right before a single `Submit()` to the compositor;
  a second `Render()` call there seems to be, by itself, incompatible with this pipeline.
- **`TransparentDepthClearFix.cs`**; instead of a second `Render()`, attach a `CommandBuffer` to the eye
  cameras (clears depth right before the "transparent" queue, where the UI lands) so it runs as part of
  their SINGLE normal pass. Confirmed with a magenta-color test that the `CommandBuffer` simply never
  executes: those cameras are outside the normal Unity render loop that `AddCommandBuffer`/`CameraEvent`
  depends on.

If this is ever revisited, the most promising lead without touching the render pipeline would be manually
smoothing the read in `CameraFollowFix.cs` (interpolating the read position ourselves) instead of touching
Rigidbody/Cinemachine; untested so far.

## Dead files (not loaded, do nothing)

- **`DialogueAdvanceFix.cs`**; left unused after fully removing controller/laser support (the user asked
  to remove it entirely and go back to keyboard/mouse + gaze pointer). Compiles but nothing calls it from
  `Plugin.cs`.

## Diagnostic hotkeys (`Plugin.cs`, always active)

- **F3**; dumps every `VideoPlayer` in the scene (mode, texture, whether it's shown on any renderer).
- **F4**; dumps every `Renderer` within 5m of the VR rig (mesh, material, shader).
- **F5**; forces `GameManager.IsInDialogue(false)` by hand, in case the flag gets stuck.
- **F6**; dumps the `FirstPersonController`'s state (can move/look) and whether the game thinks a dialogue
  is active.
- **F8**; dumps every `Canvas` (mode, size, whether it has text/RawImage, texture of those RawImages).
- **F9**; dumps every `Camera` in the scene (which one is `Camera.main`, whether they have a
  `CinemachineBrain`).

## Mod config (`BepInEx\config\com.newunitymodder.unityvrmod.cfg`)

- `VR World Scale`; 1 = normal, raise it if the world feels tiny, lower it if it feels huge.
- `User Eye Height Offset`; eye-height adjustment in meters.
- `Asserted Camera Overrides`; if UnityVRMod picks the wrong camera, this forces which GameObject/camera
  to use manually. Format: `SceneName|Path/To/Camera;`. Currently used for `MainCamera` globally.
- `Scene-Specific Pose Overrides`; per-scene initial position/rotation for the VR rig.
- `Safe Mode Level`; set to `FullVrReinitOnToggle` (recommended for OpenVR, avoids a stuck session when
  toggling VR by hand).
- `Automatic Safe Mode Duration`; how long VR rendering is disabled on every scene change. Trade-off:
  - **0.2** (current value) = more stable. At 0.1 we hit several hard, silent crashes (no C# exception,
    the BepInEx log just cuts off) right at scene transitions.
  - **0.1** = faster/less intrusive transitions, but more crash risk on scene change.
  - Important: the crashes seen at 0.1 turned out to be the **AMD graphics driver**
    (Windows exception `0xc0000005`, module "unknown", the exact same byte-for-byte stack trace inside
    `amdxx64.dll` every time, visible in `%LOCALAPPDATA%\Temp\DesbordeGames\HOPECORE\Crashes\`), not this
    mod or UnityVRMod. Before ruling out 0.1 entirely, it's worth updating/reinstalling the AMD driver and
    disabling the Radeon Software overlay (a very common cause of crashes like this in games that
    manually render to textures, like this mod does for VR).

## How to test it with the headset

1. Open SteamVR first (headset connected and on).
2. Launch the game with `-force-d3d11` (Steam launch options, or run
   `HOPECORE.exe -force-d3d11` directly).
3. Check `BepInEx\LogOutput.log`; you should see
   `[VRModCore] Unity VR Mod 0.1.0 (Mono) fully initialized.` and the `[UnityVRMod Debug-Hotkey Fix]
   [...] Patched ...` lines for each fix above.
4. The mod starts in Safe Mode. Use UnityVRMod's own Safe Mode toggle to enable stereo rendering/head
   tracking (and to fall back to Safe Mode if something goes wrong).

## Building and deploying after a change

```
cd mod\UnityVRModFix
dotnet build -c Release
copy bin\Release\UnityVRModFix.dll ..\..\BepInEx\plugins\UnityVRModFix\UnityVRModFix.dll
```

Restart the game to load the new DLL (BepInEx doesn't hot-reload plugins).
