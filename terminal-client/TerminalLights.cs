using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // the light/lamp system, ported from icebreaker's RenderEnvProbe subset: the rip
    // loses baked lighting, so interior lamps serialize at intensity 0 — revive them
    // realtime, add a flat ambient fill, and clamp bsg's native light fade window
    // (CullingManager FADES intensity across an authored 50-80m window and never
    // disables a light — far lamps bleed GPU on players even when the dev box shows
    // nothing).
    internal static class TerminalLights
    {
        // the discovered dead lamps, cached so the slider can re-drive them (once set
        // to a real intensity they no longer look "dead", so we can't re-detect)
        private static readonly List<Light> _lamps = new List<Light>();
        private static float _lastLamp = -1f, _lastAmbient = -1f, _lastLightCull = -1f;
        private static Camera _opticCam;
        private static bool _lastShadows;

        internal static bool CutsceneHold;

        internal static void ResetForNewRaid()
        {
            _lamps.Clear();
            _lastLamp = _lastAmbient = _lastLightCull = -1f;
            CutsceneHold = false;
            _sky = null;
            _skyTimeLogged = false;
        }

        // TOD HOUR SYNC — the healed sky runs at the scene's authored serialized hour
        // (night) because WeatherController (the thing that syncs TOD to session time)
        // is scrubbed until the weather port. drive the cycle straight from the raid
        // clock: icebreaker's memory names the exact failure this prevents ("no TOD
        // hour sync -> night sun -> pitch black map").
        private static TOD_Sky _sky;
        private static bool _skyTimeLogged;

        internal static void TickSkyTime()
        {
            try
            {
                if (_sky == null) _sky = UnityEngine.Object.FindObjectOfType<TOD_Sky>();
                if (_sky == null || !_sky.Initialized) return;
                // GClass4.CurrentTime never materializes on terminal (2026-08-10 raid:
                // clock host waited 20s, sky stayed daytime) — GameWorld's clock is the
                // one RaidClock anchors, fall back to it
                var gdt = GClass4.Instance?.CurrentTime?.GameDateTime
                       ?? Comfort.Common.Singleton<EFT.GameWorld>.Instance?.GameDateTime;
                if (gdt == null) return;
                var dt = gdt.Calculate();
                _sky.Cycle.DateTime = dt;
                if (!_skyTimeLogged)
                {
                    _skyTimeLogged = true;
                    Plugin.Log.LogWarning($"[Sky] TOD hour synced to raid time: {dt:HH:mm} (re-synced ~1/s)");
                }
            }
            catch { }
        }

        // near-zero non-directional light in a Terminal scene = dead lamp. additive:
        // safe to call again for streamed-in stragglers.
        // every lit non-directional light within 4m of the player, full hierarchy path —
        // the definitive "what is that glow" instrument
        internal static void DumpNearPlayerLights()
        {
            try
            {
                var mp = Comfort.Common.Singleton<EFT.GameWorld>.Instance?.MainPlayer;
                if (mp == null) { Plugin.Log.LogWarning("[LightProbe] no main player"); return; }
                int found = 0;
                foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
                {
                    if (l == null || !l.enabled || l.intensity <= 0.01f || l.type == LightType.Directional) continue;
                    if ((l.transform.position - mp.Position).sqrMagnitude > 4f * 4f) continue;
                    string path = l.transform.name;
                    for (var t = l.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
                    Plugin.Log.LogWarning($"[LightProbe] near-player light: '{path}' scene='{l.gameObject.scene.name}' "
                        + $"type={l.type} intensity={l.intensity:0.00} range={l.range:0.0} culled={l.GetComponent<CullingLightObject>() != null}");
                    found++;
                }
                if (found == 0) Plugin.Log.LogWarning("[LightProbe] no lit lights within 4m right now");
            }
            catch { }
        }

        // every playing AudioSource within earshot, loudest-closest first — clip name,
        // hierarchy path, volume, distance, rolloff. the what-is-that-noise instrument.
        internal static void DumpNearPlayerSounds()
        {
            try
            {
                var mp = Comfort.Common.Singleton<EFT.GameWorld>.Instance?.MainPlayer;
                if (mp == null) { Plugin.Log.LogWarning("[SoundProbe] no main player"); return; }
                var rows = new System.Collections.Generic.List<(float score, string line)>();
                foreach (var s in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                {
                    if (s == null || !s.isPlaying || s.volume <= 0.001f) continue;
                    float d = (s.transform.position - mp.Position).magnitude;
                    // 2D sources play at full volume everywhere REGARDLESS of where
                    // their transform sits — the 60m distance cut hid exactly the
                    // sources that sound "consistent in open areas" (2026-08-11: the
                    // global sparrow loop escaped the probe entirely). never cut those.
                    bool global = s.spatialBlend < 0.5f;
                    if (!global && d > 60f) continue;
                    string path = s.transform.name;
                    for (var t = s.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
                    rows.Add((global ? float.MaxValue : s.volume / Mathf.Max(d, 1f),
                        $"[SoundProbe] {(global ? "GLOBAL/2D " : "")}'{s.clip?.name ?? "<procedural>"}' on '{path}' "
                        + $"vol={s.volume:0.00} dist={d:0.0}m blend={s.spatialBlend:0.0} loop={s.loop} "
                        + $"min/max={s.minDistance:0.0}/{s.maxDistance:0.0} scene='{s.gameObject.scene.name}'"));
                }
                rows.Sort((a, b) => b.score.CompareTo(a.score));
                foreach (var (_, line) in rows.Take(20)) Plugin.Log.LogWarning(line);
                Plugin.Log.LogWarning($"[SoundProbe] {rows.Count} playing source(s) within 60m ({Mathf.Max(rows.Count - 20, 0)} not shown)");
            }
            catch { }
        }

        internal static void DiscoverLamps()
        {
            // lights owned by a CullingLightObject are driven by BSG's native system
            // (intensity from _maxLightIntensity + fade curves) — ours would fight it
            var nativeOwned = new HashSet<Light>();
            foreach (var clo in UnityEngine.Object.FindObjectsOfType<CullingLightObject>())
            {
                var l = clo.GetLight();
                if (l != null) nativeOwned.Add(l);
            }
            int skipped = 0;
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (l == null || l.type == LightType.Directional || l.intensity > 0.1f || _lamps.Contains(l)) continue;
                // MAP lights only — icebreaker's ungated sweep once revived the camera
                // prefab's dormant hideout flashlight and it rode the player like a halo
                var sc = l.gameObject.scene.name;
                if (sc == null || !sc.StartsWith("Terminal")) continue;
                if (nativeOwned.Contains(l)) { skipped++; continue; }
                _lamps.Add(l);
            }
            if (skipped > 0)
                Plugin.Log.LogInfo($"[Lights] {skipped} lamps native-owned (CullingLightObject) — LampIntensity drives only the {_lamps.Count} unowned");
        }

        // drive every discovered lamp from the config slider (live). native-owned
        // lights get their brightness ceiling set instead — and float_1 (the cached
        // "on" value CullingManager actually drives) must follow or nothing changes.
        internal static void ApplyLamps()
        {
            float v = Plugin.LampIntensity.Value;
            var shadows = Plugin.LampShadows.Value ? LightShadows.Hard : LightShadows.None;
            int n = 0;
            foreach (var l in _lamps)
                if (l != null)
                {
                    l.intensity = v; l.shadows = shadows; n++;
                    // slider at 0 = lights OFF, guaranteed — not "intensity 0 and hope
                    // unity skips it" (big GPU win; emissives carry the look)
                    if (v <= 0.01f) l.enabled = false;
                    else if (!l.enabled) l.enabled = true;
                }

            int natives = 0, windowed = 0;
            var fMax = AccessTools.Field(typeof(CullingLightObject), "_maxLightIntensity");
            var fCached = AccessTools.Field(typeof(CullingLightObject), "float_1");
            // LightCullDistance tightens the NATIVE fade window — one-way shrink per
            // raid; raising it back needs a raid restart
            var fFadeStart = AccessTools.Field(typeof(CullingLightObject), "_fadeStartDistance");
            var fFadeEnd = AccessTools.Field(typeof(CullingLightObject), "_fadeEndDistance");
            float dCull = Plugin.LightCullDistance.Value;
            // MANAGER-LESS MODE (terminal, unlike icebreaker): the ceilings written
            // below are only ever APPLIED to the actual Light by CullingManager's
            // visibility path — and terminal has no manager (native culling suppressed,
            // machinery stubless). drive the Light components directly ourselves.
            bool managerAlive = false;
            try { managerAlive = CullingManager.Instance != null; } catch { }
            foreach (var clo in UnityEngine.Object.FindObjectsOfType<CullingLightObject>())
            {
                var cl = clo.GetLight();
                if (cl == null) continue;
                // MAP lights only — same gate as DiscoverLamps (the halo lesson): this
                // loop used to drive EVERY CullingLightObject in the game, including any
                // riding the player/weapon rig in the main scene
                var cs = cl.gameObject.scene.name;
                if (cs == null || !cs.StartsWith("Terminal")) continue;
                fMax?.SetValue(clo, v);
                fCached?.SetValue(clo, v);
                if (!managerAlive)
                {
                    cl.intensity = v;
                    cl.shadows = shadows;
                    if (v <= 0.01f) cl.enabled = false;
                    else if (!cl.enabled) cl.enabled = true;
                }
                natives++;
                try
                {
                    if (fFadeStart != null && fFadeEnd != null && dCull < (float)fFadeEnd.GetValue(clo))
                    {
                        fFadeStart.SetValue(clo, Mathf.Min((float)fFadeStart.GetValue(clo), dCull * 0.6f));
                        fFadeEnd.SetValue(clo, dCull);
                        clo.method_3(); // recompute the squared-distance caches
                        windowed++;
                    }
                }
                catch { }
            }
            if (windowed > 0)
                Plugin.Log.LogDebug($"[Lights] native fade window tightened to {dCull:0}m on {windowed} lights");
            _lastLamp = v;
            _lastShadows = Plugin.LampShadows.Value;
            _lastLightCull = dCull;
            Plugin.Log.LogInfo($"[Lights] drove {n} plain lamps + {natives} native culling lights to intensity {v:F2}"
                + (managerAlive ? "" : " (MANAGER-LESS: Light components driven directly)"));

            // LAMP AUTOPSY — pitch-black-near-lamps discriminator: if these show
            // enabled lights with sane range/color/mask and the ground still stays
            // black, the geometry's deferred response is broken (shader rebind gap),
            // not the lights. one-shot per apply.
            try
            {
                var cam = TerminalCullingDriver.CameraRef != null ? TerminalCullingDriver.CameraRef : Camera.main;
                var camPos = cam != null ? cam.transform.position : Vector3.zero;
                int near = 0, sampled = 0;
                var sb = new System.Text.StringBuilder();
                foreach (var clo in UnityEngine.Object.FindObjectsOfType<CullingLightObject>())
                {
                    var l = clo.GetLight();
                    if (l == null) continue;
                    float d = Vector3.Distance(l.transform.position, camPos);
                    if (d < 30f) near++;
                    if (sampled < 5 && d < 60f)
                    {
                        sampled++;
                        sb.Append($"\n  '{l.name}' d={d:0}m enabled={l.enabled} type={l.type} intensity={l.intensity:0.##} range={l.range:0.#} color={l.color} mask=0x{l.cullingMask:X} shadows={l.shadows}");
                    }
                }
                Plugin.Log.LogWarning($"[Lights] AUTOPSY: {near} native lights within 30m of camera, samples:{sb}");
            }
            catch (System.Exception e) { Plugin.Log.LogDebug($"[Lights] autopsy failed: {e.Message}"); }
        }

        // flat ambient fill. retail LevelSettings re-applies its OWN ambient fields
        // every frame via Camera.onPreCull (authored black — the map relied on baked
        // lightmaps we don't have), so when the singleton exists write our fill
        // THROUGH its fields; direct RenderSettings writes are the fallback.
        internal static void ApplyAmbient()
        {
            float a = Plugin.AmbientIntensity.Value;
            _lastAmbient = a;
            var fill = new Color(0.15f * a, 0.15f * a, 0.18f * a, 1f);
            var ls = Singleton<LevelSettings>.Instance;
            if (ls != null)
            {
                ls.AmbientMode = UnityEngine.Rendering.AmbientMode.Flat;
                ls.SkyColor = fill;      // Flat mode: the native applier writes SkyColor into ambientLight
                ls.EquatorColor = fill;
                ls.GroundColor = fill;
                ls.AmbientIntensity = a;
                // NVG hemisphere too — toggling goggles swaps RenderSettings to the
                // NightVision* fields, which retail authors at 0.04 gray / intensity 0
                // (extraction 2026-08-10 — retail brightness lived in lightmaps we dont
                // have), so NVGs made the map DARKER. lifted fill: the goggle post-gain
                // amplifies whatever ambient exists.
                float nv = Plugin.NvgAmbient.Value;
                var nvFill = new Color(0.15f * a * nv, 0.18f * a * nv, 0.15f * a * nv, 1f);
                ls.NightVisionSkyColor = nvFill;
                ls.NightVisionEquatorColor = nvFill;
                ls.NightVisionGroundColor = nvFill;
                ls.NightVisionAmbientIntensity = a * nv;
                Plugin.Log.LogDebug($"[Ambient] flat ambient -> {fill}, nvg -> {nvFill} (via LevelSettings, native per-frame apply)");
            }
            else
            {
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = fill;
                RenderSettings.ambientIntensity = a;
                Plugin.Log.LogDebug($"[Ambient] flat ambient -> {fill} (RenderSettings fallback, no LevelSettings singleton)");
            }
        }

        // the native system darkens lights by fading INTENSITY, not Light.enabled —
        // restore state + intensity ourselves, per-object guarded (bsg's own
        // ForceEnable loop NREs on culling objects with null internals)
        private static System.Reflection.FieldInfo _cloMaxIntensity;
        internal static int ForceNativeLightsOn()
        {
            if (_cloMaxIntensity == null)
                _cloMaxIntensity = AccessTools.Field(typeof(CullingLightObject), "_maxLightIntensity");
            int n = 0;
            foreach (var clo in UnityEngine.Object.FindObjectsOfType<CullingLightObject>())
            {
                try { clo.SetVisibility(true); } catch { }
                var l = clo.GetLight();
                if (l == null) continue;
                if (!l.enabled) l.enabled = true;
                try
                {
                    float mi = (float)_cloMaxIntensity.GetValue(clo);
                    if (mi > 0f && l.intensity < mi) { l.intensity = mi; n++; }
                }
                catch { }
            }
            return n;
        }

        // for cutscene wide shots: the native CullingManager also rides onPreCull and
        // would cull from the PLAYER's stale position (dark map, wedged lights).
        // LockState(true) pauses evaluation WITHOUT unhooking anything — NEVER toggle
        // cm.enabled: OnDisable unhooks its onPreCull and only Awake re-registers
        // (raid-long lobotomy, icebreaker shipped that bug once).
        internal static void CutsceneShowAll()
        {
            CutsceneHold = true;
            try
            {
                var cm = CullingManager.Instance;
                if (cm != null) cm.LockState(true);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[CutsceneHold] native manager lock failed: {e.Message}"); }
            int lamps = 0;
            foreach (var l in _lamps)
                if (l != null && !l.enabled) { l.enabled = true; lamps++; }
            int native = ForceNativeLightsOn();
            Plugin.Log.LogDebug($"[CutsceneHold] armed — {lamps} lamps re-enabled, {native} native lights forced on");
        }

        // post-cutscene: heal every native light once more, then unlock the manager so
        // it resumes with fresh distances from the real camera and re-culls cleanly
        internal static void CutsceneRelease()
        {
            CutsceneHold = false;
            int native = 0;
            try { native = ForceNativeLightsOn(); } catch { }
            try
            {
                var cm = CullingManager.Instance;
                if (cm != null) cm.LockState(false);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[CutsceneHold] release failed: {e.Message}"); }
            Plugin.Log.LogDebug($"[CutsceneHold] released — healed {native} native lights, culling unlocked");
        }

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_LightsAtRaidStart
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!TerminalGate.On) return;
                var go = new GameObject("Terminal_Lights");
                go.AddComponent<Host>();
            }
        }

        // staged init (one system per frame — icebreaker's all-in-one-frame stage was
        // a 14s load-in hitch), then a light ticker for live config re-apply
        internal class Host : MonoBehaviour
        {
            private void Start() => StartCoroutine(Init());

            private IEnumerator Init()
            {
                DiscoverLamps(); yield return null;
                ApplyLamps(); yield return null;
                ApplyAmbient(); yield return null;
                // after ApplyLamps on purpose: VolumetricLight.CheckIntensity refuses
                // to register a beam whose light sits under 0.001, and the lamps are
                // authored at 0 until the pass above lights them
                TerminalVolumetricLights.Restore(); yield return null;
                try { TerminalFlares.TryBuild(); }
                catch (System.Exception e) { Plugin.Log.LogWarning($"[Flares] build failed: {e.Message}"); }
            }

            private void Update()
            {
                if (Time.frameCount % 60 == 17) TickSkyTime();
                // acoustics tier 2: build once (idempotent, liveness-tracked), then
                // self-drive the env poll — BSG's own async driver dies silently on the
                // first half-spawned player exception
                if (Plugin.SpatialAudio.Value)
                {
                    if (Time.frameCount % 120 == 7)
                    {
                        TerminalAcoustics.TickSpatialInit(); // rooms first — the ambient gating rides on them
                        TerminalAcoustics.TickSpatialVerdict();
                        TerminalAcoustics.TryBuildEnvironmentTriggers();
                        // the authored layer first; the interim volume/governor pass
                        // stands itself down once that succeeds
                        if (Plugin.AmbientRetail.Value) TerminalAcoustics.TryStageAmbient();
                        TerminalAcoustics.TryApplyAmbientAuthoring();
                    }
                    if ((Time.frameCount & 1) == 0) TerminalAcoustics.DriveEnvironment();
                }
                // the player-halo hunt (2026-08-10, "light appears when i mag the AK"):
                // manual key (user call — no DevMode required) or DevMode interval —
                // names every lit light near the player so the culprit stops being a
                // theory
                if (Plugin.LightProbeKey.Value.IsDown() || (Plugin.DevMode.Value && Time.frameCount % 300 == 33))
                    DumpNearPlayerLights();
                // the loud-clip hunt (2026-08-11: rat-squeak + chain-jingle ambients too
                // loud, too frequent): name every audibly-playing source near the player
                if (Plugin.SoundProbeKey.Value.IsDown())
                    DumpNearPlayerSounds();
                // magnified scopes render through BaseOpticCamera(Clone) which ships its
                // OWN TOD_Scattering — it fogs the whole magnified view toward the black
                // night sky (icebreaker lesson: dark scopes). the cam only registers
                // while aiming, so poll Camera.allCameras (a handful) until cached, then
                // keep the component dead.
                if (_opticCam == null)
                {
                    var cams = Camera.allCameras;
                    for (int i = 0; i < cams.Length; i++)
                        if (cams[i] != null && cams[i].name == "BaseOpticCamera(Clone)") { _opticCam = cams[i]; break; }
                }
                if (_opticCam != null)
                {
                    var os = _opticCam.GetComponent<TOD_Scattering>();
                    if (os != null && os.enabled)
                    {
                        os.enabled = false;
                        Plugin.Log.LogInfo("[Lights] optic camera TOD_Scattering disabled (dark magnified scopes)");
                    }
                }
                if (Time.frameCount % 30 != 0) return;
                if (Plugin.LampIntensity.Value != _lastLamp
                    || Plugin.LampShadows.Value != _lastShadows
                    || Plugin.LightCullDistance.Value != _lastLightCull)
                    ApplyLamps();
                if (Plugin.AmbientIntensity.Value != _lastAmbient)
                    ApplyAmbient();
            }
        }
    }
}
