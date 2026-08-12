using System;
using System.Collections;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // retail 1.0's PortCutscene intro (Terminal_area_PortCutscene_00_Intro, timeline
    // rebuilt via SDK Author 21) played at MAP START, unskippable — retail plays it
    // the moment the raid begins, unlike icebreaker's story-beat trigger.
    //
    // trimmed port of IcebreakerTimelineCutscene: same additive-load/drive/unload
    // shape, same onPreCull camera copy (fires after every LateUpdate writer, so the
    // rig gets the last word while the real camera keeps its post stack). the
    // icebreaker-specific holds (volumetrics, volfog profile, native-culling
    // LockState/ForceEnable, interior draw-off) come back as those systems get
    // ported — the culling hold especially: wide shots WILL wedge lamps dark once
    // CullingManager content is restored (see docs/memory + the dark-stern bug).
    public class TerminalIntroCutscene : MonoBehaviour
    {
        // scene ships in the SCENE bundle but NOT the preset — zero cost outside
        // playback, loaded additively here
        private const string SceneName = "Terminal_area_PortCutscene_00_Intro";
        private const float FadeDur = 0.7f;

        // map props the cutscene camera must not see — hidden under the fade before
        // the first cutscene frame, restored on exit (icebreaker's helipad pattern).
        // exact GO name match, searched in the named preset scene only.
        private static readonly (string scene, string name)[] HideDuringCutscene =
        {
            ("Terminal_Design_Stuff", "Inside_Door_Plastic_07_R_210-100 (1)"),
        };

        private static bool _playedThisRaid;
        // when the intro handed control back (realtime) — the attack cutscene's
        // trigger anchors on this (retail: attack fires ~45s after intro spawn-in).
        // set once by Restore(), bail paths included.
        internal static float FinishedAt = -1f;

        internal static void ResetForNewRaid()
        {
            _playedThisRaid = false;
            FinishedAt = -1f;
        }

        public static bool Available
        {
            get
            {
                try { return Application.CanStreamedLevelBeLoaded(SceneName); }
                catch { return false; }
            }
        }

        // map start: OnGameStarted fires once the world is up, before the player has
        // done anything — the retail timing for this cutscene
        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_PlayAtRaidStart
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!TerminalGate.On || _playedThisRaid) return;
                if (!Available)
                {
                    Plugin.Log.LogWarning($"[IntroCutscene] '{SceneName}' not loadable (bundle missing the scene?) — skipped");
                    return;
                }
                _playedThisRaid = true;
                var go = new GameObject("Terminal_IntroCutscene");
                go.AddComponent<TerminalIntroCutscene>();
            }
        }

        private Scene _scene;
        private bool _sceneLoaded;
        private PlayableDirector _director;
        private Camera _rigCam;            // the scene's animated camera (disabled, pose source)
        private Camera _realCam;
        private GameObject _fadeCanvas;    // CutsceneRoot/UI_FadeToBlack — retail's own fade rig
        private UnityEngine.UI.Image _fadeImage;
        private bool _driving;
        private bool _inputLocked;
        private readonly List<Canvas> _hiddenCanvases = new List<Canvas>();
        private readonly List<Renderer> _hiddenRenderers = new List<Renderer>();
        private readonly List<(GameObject go, bool wasActive)> _hiddenProps = new List<(GameObject, bool)>();
        private readonly List<Behaviour> _pausedFx = new List<Behaviour>();
        private float _fade;
        private Texture2D _black;
        private bool _restored;
        private float _savedFov = -1f;     // DriveCamera stomps fov — restore on exit

        private void Start() => StartCoroutine(Run());

        private IEnumerator Run()
        {
            GamePlayerOwner.SetIgnoreInputInNPCDialog(true);
            _inputLocked = true;
            yield return Fade(0f, 1f);

            var load = SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Additive);
            if (load == null) { Bail("LoadSceneAsync returned null"); yield break; }
            while (!load.isDone) yield return null;
            _scene = SceneManager.GetSceneByName(SceneName);
            _sceneLoaded = _scene.IsValid() && _scene.isLoaded;
            if (!_sceneLoaded) { Bail("cutscene scene failed to load"); yield break; }

            // the scene arrived after the raid-start rebind pass — its materials still
            // hold the bundle's broken blobs (white in deferred). rebind now, under
            // the fade; the sceneLoaded capture hook already fenced them as ours.
            try { TerminalShaderRebind.RebindNow(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[IntroCutscene] shader rebind failed: {e.Message}"); }

            // any beams authored inside the cutscene scene were deferred at raid start
            // (the scene didn't exist yet) — idempotent, claims only new lights
            try { TerminalVolumetricLights.Restore(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[IntroCutscene] volumetric pass failed: {e.Message}"); }

            // hold the native culler + force lights on for the duration: wide shots
            // cull from the PLAYER's stale position otherwise (dark map, wedged
            // lights — the dark-stern bug). Restore() releases and it re-culls.
            try { TerminalLights.CutsceneShowAll(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[IntroCutscene] culler hold failed: {e.Message}"); }

            // director sits at the scene root ("TimeLineDirector") — search all roots,
            // inactive included
            foreach (var rgo in _scene.GetRootGameObjects())
            {
                _director = rgo.GetComponentInChildren<PlayableDirector>(true);
                if (_director != null) break;
            }
            if (_director == null || _director.playableAsset == null)
            { Bail("TimeLineDirector missing or playableAsset empty (Author 21 not run?)"); yield break; }

            // rig camera: first camera anywhere in the scene
            foreach (var rgo in _scene.GetRootGameObjects())
            {
                _rigCam = rgo.GetComponentInChildren<Camera>(true);
                if (_rigCam != null) break;
            }
            if (_rigCam == null) { Bail("no camera in cutscene scene"); yield break; }
            // pose/FOV source only — must never render or listen
            _rigCam.enabled = false;
            var lst = _rigCam.GetComponent<AudioListener>();
            if (lst != null) lst.enabled = false;

            _realCam = CameraClass.Instance?.Camera;
            if (_realCam == null) _realCam = Camera.main;
            if (_realCam == null) { Bail("no main camera"); yield break; }

            // pull user-flagged map props before the first frame is ever drawn —
            // we're still under the black fade here
            foreach (var (sceneName, goName) in HideDuringCutscene)
            {
                var scn = SceneManager.GetSceneByName(sceneName);
                var go = FindInScene(scn, goName);
                if (go == null)
                {
                    Plugin.Log.LogWarning($"[IntroCutscene] hide target '{goName}' not found in '{sceneName}'");
                    continue;
                }
                _hiddenProps.Add((go, go.activeSelf));
                go.SetActive(false);
                Plugin.Log.LogDebug($"[IntroCutscene] hid '{goName}' for the cutscene");
            }

            // retail's own fade rig — activate for playback, off on exit (the timeline
            // drives FadeToBlackImage's alpha itself). the rip loses the canvas setup
            // AND the image color (first raid: WHITE fade in a screen-corner rect) —
            // rebuild both: overlay canvas, stretch rect, black rgb.
            var fadeTf = FindInScene(_scene, "UI_FadeToBlack");
            _fadeCanvas = fadeTf != null ? fadeTf : null;
            if (_fadeCanvas != null)
            {
                _fadeCanvas.SetActive(true);
                try
                {
                    foreach (var cv in _fadeCanvas.GetComponentsInChildren<Canvas>(true))
                    {
                        cv.renderMode = RenderMode.ScreenSpaceOverlay;
                        cv.sortingOrder = 9999;
                    }
                    foreach (var cs in _fadeCanvas.GetComponentsInChildren<UnityEngine.UI.CanvasScaler>(true))
                    {
                        cs.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                        cs.referenceResolution = new Vector2(1920f, 1080f);
                    }
                    _fadeImage = _fadeCanvas.GetComponentInChildren<UnityEngine.UI.Image>(true);
                    if (_fadeImage != null)
                    {
                        var rt = _fadeImage.rectTransform;
                        rt.anchorMin = Vector2.zero;
                        rt.anchorMax = Vector2.one;
                        rt.offsetMin = Vector2.zero;
                        rt.offsetMax = Vector2.zero;
                        _fadeImage.color = new Color(0f, 0f, 0f, _fadeImage.color.a);
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[IntroCutscene] fade rig fix failed: {e.Message}"); }
            }

            // pause player screen effects (painkiller B&W, contusion vignette...) —
            // CC_* image effects driven by EffectsController would grade the cutscene.
            // disable the driver first so it can't re-enable them mid-playback.
            foreach (var b in _realCam.GetComponents<Behaviour>())
                if (b != null && b.enabled && (b is EffectsController || b is CC_Base))
                {
                    b.enabled = false;
                    _pausedFx.Add(b);
                }

            // hide the player — the flying camera would film them standing at spawn
            var player = Singleton<GameWorld>.Instance?.MainPlayer;
            if (player != null)
                foreach (var r in player.gameObject.GetComponentsInChildren<Renderer>(false))
                    if (r != null && r.enabled) { r.enabled = false; _hiddenRenderers.Add(r); }

            // hide the HUD, but never the cutscene's own canvases
            foreach (var cv in UnityEngine.Object.FindObjectsOfType<Canvas>())
                if (cv != null && cv.enabled && cv.isRootCanvas
                    && cv.renderMode == RenderMode.ScreenSpaceOverlay
                    && cv.gameObject.scene != _scene)
                {
                    cv.enabled = false;
                    _hiddenCanvases.Add(cv);
                }

            _savedFov = _realCam.fieldOfView;
            Camera.onPreCull += DriveCamera;
            _driving = true;

            _director.extrapolationMode = DirectorWrapMode.Hold; // no loop surprises
            _director.Play();
            Plugin.Log.LogInfo($"[IntroCutscene] playing '{_director.playableAsset.name}' " +
                               $"({_director.duration:0.0}s, unskippable)");
            yield return Fade(1f, 0f);

            // unskippable: runs to the end, no input checked. hard stop only as a
            // safety net against a director that never finishes.
            double dur = _director.duration;
            float hardStop = Time.realtimeSinceStartup + (float)dur + 10f;
            while (Time.realtimeSinceStartup < hardStop)
            {
                if (_director == null) break;
                if (_director.state != PlayState.Playing) break;
                if (_director.time >= dur - 0.05) break;
                // the timeline may animate the fade image's FULL color (white keys from
                // the rip) — enforce black rgb per frame, alpha stays the timeline's
                if (_fadeImage != null)
                {
                    var fc = _fadeImage.color;
                    if (fc.r > 0f || fc.g > 0f || fc.b > 0f)
                        _fadeImage.color = new Color(0f, 0f, 0f, fc.a);
                }
                yield return null;
            }

            yield return Fade(0f, 1f);
            Restore();
            if (_sceneLoaded)
            {
                var unload = SceneManager.UnloadSceneAsync(_scene);
                while (unload != null && !unload.isDone) yield return null;
                _sceneLoaded = false;
            }
            // brief linger at black, then reveal — gives late systems a few frames to
            // settle for the returned camera before anything pops on screen
            yield return new WaitForSecondsRealtime(0.8f);
            yield return Fade(1f, 0f, 1.6f);
            Destroy(gameObject);
        }

        // by name anywhere in the loaded scene, inactive included
        private static GameObject FindInScene(Scene scn, string name)
        {
            if (!scn.IsValid() || !scn.isLoaded) return null;
            foreach (var rgo in scn.GetRootGameObjects())
            {
                if (rgo.name == name) return rgo;
                foreach (var t in rgo.GetComponentsInChildren<Transform>(true))
                    if (t != null && t.name == name) return t.gameObject;
            }
            return null;
        }

        private void DriveCamera(Camera cam)
        {
            if (cam != _realCam || _rigCam == null) return;
            var t = _rigCam.transform;
            cam.transform.SetPositionAndRotation(t.position, t.rotation);
            cam.fieldOfView = _rigCam.fieldOfView;
        }

        private void Bail(string why)
        {
            // a broken timeline must never brick raid start — restore and play on
            Plugin.Log.LogWarning($"[IntroCutscene] {why} — cutscene skipped");
            Restore();
            if (_sceneLoaded) { SceneManager.UnloadSceneAsync(_scene); _sceneLoaded = false; }
            Destroy(gameObject);
        }

        // idempotent — scripted exit + OnDestroy teardown net
        private void Restore()
        {
            if (_restored) return;
            _restored = true;
            if (FinishedAt < 0f) FinishedAt = Time.realtimeSinceStartup;
            if (_driving) { Camera.onPreCull -= DriveCamera; _driving = false; }
            try { TerminalLights.CutsceneRelease(); } catch { }
            if (_realCam != null && _savedFov > 0f) _realCam.fieldOfView = _savedFov;
            if (_director != null && _director.state == PlayState.Playing) _director.Stop();
            if (_fadeCanvas != null) { _fadeCanvas.SetActive(false); _fadeCanvas = null; }
            // hand the props back as we found them — a bail must not leave the map
            // short a door
            foreach (var (go, wasActive) in _hiddenProps) if (go != null) go.SetActive(wasActive);
            _hiddenProps.Clear();
            foreach (var r in _hiddenRenderers) if (r != null) r.enabled = true;
            _hiddenRenderers.Clear();
            foreach (var b in _pausedFx) if (b != null) b.enabled = true;
            _pausedFx.Clear();
            foreach (var cv in _hiddenCanvases) if (cv != null) cv.enabled = true;
            _hiddenCanvases.Clear();
            if (_inputLocked) { GamePlayerOwner.SetIgnoreInputInNPCDialog(false); _inputLocked = false; }
        }

        private IEnumerator Fade(float from, float to, float dur = FadeDur)
        {
            float t = 0f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                _fade = Mathf.Lerp(from, to, t / dur);
                yield return null;
            }
            _fade = to;
        }

        private void OnGUI()
        {
            if (_fade <= 0f) return;
            if (_black == null)
            {
                _black = new Texture2D(1, 1);
                _black.SetPixel(0, 0, Color.black);
                _black.Apply();
            }
            GUI.depth = -10000;
            GUI.color = new Color(0f, 0f, 0f, _fade);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _black);
            GUI.color = Color.white;
        }

        private void OnDestroy()
        {
            Restore();
            if (_sceneLoaded) { SceneManager.UnloadSceneAsync(_scene); _sceneLoaded = false; }
            if (_black != null) Destroy(_black);
        }
    }
}
