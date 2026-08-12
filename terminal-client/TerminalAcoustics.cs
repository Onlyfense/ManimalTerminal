using System;
using System.Collections.Generic;
using System.Linq;
using SysIoPath = System.IO.Path;
using Audio.AmbientSubsystem;
using Audio.SpatialSystem;
using Audio.SpatialSystem.Data;
using Audio.SpatialSystem.Utils;
using EFT.EnvironmentEffect;
using EFT.Interactive;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // SPATIAL AUDIO + ENVIRONMENT LAYER — icebreaker's IcebreakerAcoustics port,
    // spatial tiers only (their hand-authored ambient-bed mixer stays behind; the
    // terminal sound rig owns that role here). the retail authoring survived as raw
    // serialized bytes — extract_terminal_spatial_audio.py decodes it into
    // plugin-data/acoustics/terminal_spatial_audio.json:
    //   tier 2: EnvironmentManager + 7 TriggerGroups + 64 IndoorTriggers from
    //           recovered world transforms — indoor/outdoor banks, exposure, rain.
    //   tier 3: 104 SpatialAudioRooms / 345 portals / 158 AudioTriggerAreas onto the
    //           bundled Terminal_Sound GOs, location-info + occlusion assets rebuilt
    //           from recovered values, BSG's own terminal_sound.audiobakedata into
    //           StreamingAssets, then the REAL SpatialAudioSystem.Initialize runs.
    // staging failure at any point returns false -> TerminalAudioFixes falls back to
    // the old skip-init + occlusion-airbag mode. the bake FILE ships from the user's
    // 1.0 client dump (StreamingAssets/AudioBakeData/terminal_sound.audiobakedata)
    // into plugin-data/acoustics/ — staging reports loudly when it's missing.
    internal static class TerminalAcoustics
    {
        private static JObject _sidecar;
        private static bool _sidecarTried;
        // liveness-tracked, not bools: built objects die with the raid scenes, and the
        // next raid must rebuild (unity fake-null does the staleness detection)
        private static GameObject _envRoot;
        private static GameObject _spatialMarker;

        private static string DataDir =>
            SysIoPath.Combine(SysIoPath.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "plugin-data", "acoustics");

        private static JObject Sidecar()
        {
            if (_sidecarTried) return _sidecar;
            _sidecarTried = true;
            try
            {
                var path = SysIoPath.Combine(DataDir, "terminal_spatial_audio.json");
                if (!System.IO.File.Exists(path))
                {
                    Plugin.Log.LogDebug($"[Acoustics] no sidecar at {path} — spatial audio + env triggers stay off");
                    return null;
                }
                _sidecar = JObject.Parse(System.IO.File.ReadAllText(path));
                Plugin.Log.LogInfo("[Acoustics] sidecar loaded");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Acoustics] sidecar parse failed: {e.Message}");
                _sidecar = null;
            }
            return _sidecar;
        }

        // ------------------------------------------------------- system resurrection
        // TarkovApplication calls SpatialAudioSystem.Initialize ONLY when the
        // MonoBehaviourSingleton is instantiated — and the singleton registers in the
        // component's Awake. the rip shipped terminal's system as a hollow missing
        // script, so the gate was always false and Initialize never ran (2026-08-11
        // raid: staged nothing, skipped nothing). create the component the moment the
        // Sound scene loads; Awake registers the singleton, the application's own call
        // path then Initializes it through our staging prefix. registered from
        // Plugin.Awake via SceneManager.sceneLoaded.
        public static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != "Terminal_Sound" || !Plugin.SpatialAudio.Value) return;
            try
            {
                if (MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated) return;
                if (Sidecar() == null) return; // no data, no system — airbags handle it

                // retail hosts it on the scene-root GO named 'SpatialAudioSystem'
                GameObject host = null;
                foreach (var root in scene.GetRootGameObjects())
                    if (root.name == "SpatialAudioSystem") { host = root; break; }
                if (host == null)
                {
                    host = new GameObject("SpatialAudioSystem");
                    SceneManager.MoveGameObjectToScene(host, scene);
                }
                if (!host.activeSelf)
                {
                    host.SetActive(true); // Awake must run or the singleton never registers
                    Plugin.Log.LogDebug("[Acoustics] SpatialAudioSystem host was inactive — activated");
                }
                if (host.GetComponent<SpatialAudioSystem>() == null)
                {
                    host.AddComponent<SpatialAudioSystem>();
                    Plugin.Log.LogInfo("[Acoustics] SpatialAudioSystem component created on the Sound scene — staging will run at Initialize");
                }
                _spatialKick = false; // fresh raid, drive Initialize again if the loader misses it
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Acoustics] system resurrection failed: {e.Message}"); }
        }

        // ------------------------------------------------------------------ tier 2: env
        public static bool TryBuildEnvironmentTriggers()
        {
            if (_envRoot != null) return true;
            var sc = Sidecar();
            var triggers = sc?["scenes"]?["Terminal_Scripts"]?["IndoorTrigger"] as JArray;
            if (triggers == null || triggers.Count == 0) return false;

            try
            {
                var root = new GameObject("Terminal_EnvSwitch");
                var groupGo = new GameObject("TriggerGroup");
                groupGo.transform.SetParent(root.transform, false);

                int built = 0;
                foreach (var row in triggers)
                {
                    var trs = row["world_trs"] ?? row["trs"];
                    if (trs == null) continue;
                    var go = new GameObject($"IndoorTrigger_{built}");
                    go.transform.SetParent(groupGo.transform, false);
                    go.transform.position = V3(trs["pos"]);
                    go.transform.rotation = Q4(trs["rot"]);
                    go.transform.localScale = V3(trs["scale"]);
                    var it = go.AddComponent<IndoorTrigger>(); // Awake self-heals Bounds from transform
                    var f = row["fields"];
                    if (f != null)
                    {
                        it.IsBunker = f.Value<bool?>("IsBunker") ?? false;
                        it.FadeTime = f.Value<float?>("FadeTime") ?? 1f;
                        it.BunkerDepth = f.Value<float?>("BunkerDepth") ?? -15f;
                        it.BunkerLowPass = f.Value<float?>("BunkerLowPass") ?? 300f;
                        it.ExposureSpeed = f.Value<float?>("ExposureSpeed") ?? 4f;
                        it.ExposureOffset = f.Value<float?>("ExposureOffset") ?? 0.14f;
                        it.RainVolume = f.Value<float?>("RainVolume") ?? 0.7f;
                        it.Reinit();
                    }
                    built++;
                }

                groupGo.AddComponent<TriggerGroup>(); // Awake -> Reinit over the children above

                root.SetActive(false);
                var em = root.AddComponent<EnvironmentManager>();
                var emRow = (sc["scenes"]?["Terminal_Scripts"]?["EnvironmentManager"] as JArray)?.FirstOrDefault();
                if (emRow?["fields"] is JObject emFields)
                    FillFields(em, emFields, name => name != "Bounds");
                root.SetActive(true);

                _envRoot = root;
                Plugin.Log.LogInfo($"[Acoustics] environment switcher rebuilt: {built} indoor triggers (indoor/outdoor audio + exposure live)");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Acoustics] env trigger build failed: {e}");
                return false;
            }
        }

        // ------------------------------------------------- tier 4: THE AMBIENT LAYER
        // retail's authored ambient system, rebuilt from the extraction: 44 SoundBanks
        // (names/volumes/pitches/clip pools carved out of the 1.0 assets) feeding 1375
        // components — players, sound points, bezier splines and their emitters — all
        // hanging under the single 'AmbientAudioSystem' root GO.
        //
        // ORDER IS LOAD-BEARING (the registry-wiring regression class): the system's
        // Awake collects its workers with GetComponentsInChildren, so every component
        // must EXIST before it wakes. the whole subtree is therefore staged with the
        // root deactivated, the system component added last, and one reactivation at
        // the end — which also gives every BSG Awake correct field values on the first
        // read (the same deactivated-create discipline the spatial tier uses).
        private static GameObject _ambientRoot;
        private static readonly Dictionary<string, UnityEngine.Object> _banks =
            new Dictionary<string, UnityEngine.Object>();
        private static readonly HashSet<string> _bankClipMisses = new HashSet<string>();

        // creation order: content providers first, consumers after. every one of these
        // is alive in 4.0's Assembly-CSharp (verified 2026-08-11); the season/event
        // variants are deliberately absent — their 1.0 preset structs still drift and
        // their rows carry parse_error.
        private static readonly string[] AmbientClasses =
        {
            "BezierSpline", "SoundPoint", "SoundPointsManager",
            "AmbientSoundPlayer", "LoopAmbientSoundPlayer", "OneShotAmbientSoundPlayer",
            "WeatherRandomAmbientSoundPlayer", "AmbientSoundPlayerGroup",
            "SoundPlayerRandomPointComponent", "SoundPlayerSplineTrigger",
            "AmbientPlayerSplineMappedEmitter", "SplineEmitterPathMover",
            "SoundAmbientZoneCalculator", "SoundPlayerRoomObserverComponent",
            "AmbientSoundPlayerGroupController", "AmbientSplineEmitterController",
            "SplineTriggerChecker", "AmbientPlayerAutoPanner",
            "PrecipitationAmbientBlender", "AmbientSoundBlender",
            "EnvironmentSoundBlendSystem",
        };

        private static Type ResolveType(string name)
        {
            var t = AccessTools.TypeByName(name);
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var cand in asm.GetTypes())
                        if (cand.Name == name || cand.FullName == name) return cand;
                }
                catch { }
            }
            return null;
        }

        internal static bool AmbientStaged => _ambientRoot != null;

        // EFT.SoundBank instances from the carved retail data. only PickSingleClip ->
        // Environments[0][0] matters to the players, so one variety carrying the whole
        // authored pool behaves like the real asset (icebreaker-proven) — but unlike
        // the rig's builder this also restores the AUTHORED scalars, which is the
        // whole point of the rebuild (a bank asking for 0.15 must not play at 1.0).
        private static void BuildBanks(JObject sc)
        {
            if (_banks.Count > 0) return;
            var banks = sc["banks"] as JObject;
            if (banks == null) { Plugin.Log.LogWarning("[Ambient] sidecar carries no banks section"); return; }
            var bankType = ResolveType("EFT.SoundBank");
            var envType = ResolveType("EFT.EnvironmentVariety");
            var distType = ResolveType("EFT.DistanceVarity");
            if (bankType == null || envType == null || distType == null)
            {
                Plugin.Log.LogWarning("[Ambient] SoundBank types missing — ambient players would stay silent");
                return;
            }

            int built = 0, empty = 0;
            foreach (var kv in banks)
            {
                var row = kv.Value as JObject;
                if (row == null) continue;
                var clips = new List<AudioClip>();
                foreach (var n in (row["clipNames"] as JArray) ?? new JArray())
                {
                    var cn = n.Value<string>();
                    if (string.IsNullOrEmpty(cn)) continue;
                    var clip = FindClip(cn);
                    if (clip != null) clips.Add(clip);
                    else _bankClipMisses.Add(cn);
                }
                if (clips.Count == 0) { empty++; continue; }

                var bank = ScriptableObject.CreateInstance(bankType);
                bank.name = row.Value<string>("name") ?? kv.Key;
                var dist = Activator.CreateInstance(distType);
                AccessTools.Field(distType, "Clips").SetValue(dist, clips.ToArray());
                var variety = Activator.CreateInstance(envType);
                var vClips = Array.CreateInstance(distType, 1);
                vClips.SetValue(dist, 0);
                AccessTools.Field(envType, "Clips").SetValue(variety, vClips);
                var envs = Array.CreateInstance(envType, 1);
                envs.SetValue(variety, 0);
                AccessTools.Field(bankType, "Environments").SetValue(bank, envs);

                if (row["fields"] is JObject bf)
                {
                    FillFields(bank, bf, f => f != "SourceType");     // enum-by-int, set below
                    var st = bf.Value<int?>("SourceType");
                    var stField = AccessTools.Field(bankType, "SourceType");
                    if (st.HasValue && stField != null)
                        try { stField.SetValue(bank, Enum.ToObject(stField.FieldType, st.Value)); } catch { }
                }
                // one variety only — a bank claiming per-environment sets would index
                // past our single slot
                AccessTools.Field(bankType, "HasEnvironment")?.SetValue(bank, false);

                _banks[kv.Key] = bank;
                built++;
            }
            Plugin.Log.LogInfo($"[Ambient] {built} retail sound bank(s) rebuilt with authored volumes"
                + (empty > 0 ? $", {empty} skipped (no clips in bundle)" : ""));
        }

        public static bool TryStageAmbient()
        {
            if (_ambientRoot != null) return true;
            var sc = Sidecar();
            var sound = sc?["scenes"]?["Terminal_Sound"] as JObject;
            if (sound == null) return false;

            var sysRow = Rows(sound, "AmbientAudioSystem").FirstOrDefault();
            var rootPath = sysRow?.Value<string>("go") ?? "AmbientAudioSystem";
            var goIndex = BuildGoIndex();
            if (!goIndex.TryGetValue(rootPath, out var roots) || roots.Count == 0)
            {
                Plugin.Log.LogDebug($"[Ambient] root '{rootPath}' not in the scene yet — retrying");
                return false;
            }
            var root = roots[0].gameObject;

            try
            {
                _clipCache = null; // clip instances are per raid
                _bankClipMisses.Clear();
                BuildBanks(sc);
                if (_banks.Count == 0) return false;

                bool wasActive = root.activeSelf;
                root.SetActive(false); // ONE toggle for the whole subtree

                var comps = new Dictionary<long, Component>();
                int created = 0, occupied = 0, missing = 0;
                foreach (var cls in AmbientClasses)
                {
                    var type = ResolveType(cls);
                    if (type == null) continue;
                    foreach (var row in Rows(sound, cls))
                    {
                        if (row["parse_error"] != null) continue;
                        var t = FindGo(goIndex, row);
                        if (t == null) { missing++; continue; }
                        // the cutscene rig may already own this GO — first attach wins,
                        // two players on one object is doubled audio
                        var existing = t.gameObject.GetComponent(type);
                        if (existing != null)
                        {
                            comps[row.Value<long>("path_id")] = existing;
                            occupied++;
                            continue;
                        }
                        try
                        {
                            comps[row.Value<long>("path_id")] = t.gameObject.AddComponent(type);
                            created++;
                        }
                        catch (Exception e)
                        {
                            Plugin.Log.LogWarning($"[Ambient] {cls} on '{row.Value<string>("go")}': {e.Message}");
                        }
                    }
                }

                int filled = 0;
                foreach (var cls in AmbientClasses)
                {
                    foreach (var row in Rows(sound, cls))
                    {
                        if (row["parse_error"] != null) continue;
                        if (!comps.TryGetValue(row.Value<long>("path_id"), out var comp) || comp == null) continue;
                        if (!(row["fields"] is JObject f)) continue;
                        try
                        {
                            // plain values first (volumes, distances, curves, flags),
                            // then object refs the generic filler deliberately skips
                            FillFields(comp, f, null);
                            WireRefs(comp, f, comps);
                            BindContent(comp, row, f);
                            filled++;
                        }
                        catch (Exception e)
                        {
                            Plugin.Log.LogWarning($"[Ambient] fill {cls} '{row.Value<string>("go")}': {e.Message}");
                        }
                    }
                }

                // the system LAST: its Awake harvests the workers above via
                // GetComponentsInChildren, so it must never wake into an empty tree
                var sysType = ResolveType("AmbientAudioSystem");
                if (sysType != null && root.GetComponent(sysType) == null)
                {
                    var sys = root.AddComponent(sysType);
                    if (sysRow?["fields"] is JObject sf) { try { FillFields(sys, sf, null); } catch { } }
                }

                root.SetActive(true);
                if (!wasActive) Plugin.Log.LogDebug("[Ambient] root GO was inactive in the bundle — activated");

                // Initialize: NetworkGame calls this for online raids; offline nobody
                // does, so drive it ourselves once the tree is awake
                try
                {
                    var sysComp = root.GetComponent(sysType);
                    if (sysComp != null)
                    {
                        var initialized = AccessTools.PropertyGetter(sysType, "Initialized");
                        bool already = initialized != null && (bool)initialized.Invoke(sysComp, null);
                        if (!already) AccessTools.Method(sysType, "Initialize")?.Invoke(sysComp, null);
                        Plugin.Log.LogInfo("[Ambient] AmbientAudioSystem initialized");
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Ambient] system init failed: {e.Message}"); }

                _ambientRoot = root;
                Plugin.Log.LogInfo($"[Ambient] AMBIENT LAYER STAGED: {created} component(s) rebuilt, {filled} filled, "
                    + $"{_banks.Count} banks"
                    + (occupied > 0 ? $", {occupied} left to the sound rig" : "")
                    + (missing > 0 ? $", {missing} GO(s) not in the bundle" : ""));
                if (_bankClipMisses.Count > 0)
                    Plugin.Log.LogWarning($"[Ambient] {_bankClipMisses.Count} bank clip(s) missing from the bundle "
                        + $"(rerun Author 26 + rebuild): {string.Join(", ", _bankClipMisses.Take(6).ToArray())}"
                        + (_bankClipMisses.Count > 6 ? "..." : ""));
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Ambient] staging failed (interim authoring keeps the map alive): {e}");
                try { root.SetActive(true); } catch { }
                return false;
            }
        }

        // component/asset refs the generic value filler skips: {"ref": path_id} and
        // arrays of them, mapped onto the freshly created components
        private static void WireRefs(Component target, JObject fields, Dictionary<long, Component> comps)
        {
            var type = target.GetType();
            foreach (var prop in fields.Properties())
            {
                var fi = AccessTools.Field(type, prop.Name);
                if (fi == null) continue;
                try
                {
                    if (prop.Value is JObject o && o["ref"] != null)
                    {
                        var c = Ref(comps, o);
                        if (c != null && fi.FieldType.IsInstanceOfType(c)) fi.SetValue(target, c);
                    }
                    else if (prop.Value is JArray arr && arr.Count > 0 && arr[0] is JObject first && first["ref"] != null)
                    {
                        var elem = fi.FieldType.IsArray ? fi.FieldType.GetElementType()
                            : fi.FieldType.IsGenericType ? fi.FieldType.GetGenericArguments()[0] : null;
                        if (elem == null) continue;
                        var items = new List<Component>();
                        foreach (var tok in arr)
                        {
                            var c = Ref(comps, tok);
                            if (c != null && elem.IsInstanceOfType(c)) items.Add(c);
                        }
                        if (items.Count == 0) continue;
                        if (fi.FieldType.IsArray)
                        {
                            var a = Array.CreateInstance(elem, items.Count);
                            for (int i = 0; i < items.Count; i++) a.SetValue(items[i], i);
                            fi.SetValue(target, a);
                        }
                        else
                        {
                            var list = (System.Collections.IList)Activator.CreateInstance(fi.FieldType);
                            foreach (var it in items) list.Add(it);
                            fi.SetValue(target, list);
                        }
                    }
                }
                catch { /* drifted field — keep the class default */ }
            }
        }

        // the content refs: banks by extraction key, loop clips by name
        private static void BindContent(Component comp, JToken row, JObject fields)
        {
            var type = comp.GetType();
            var bankKey = row.Value<string>("_bankKey");
            if (!string.IsNullOrEmpty(bankKey) && _banks.TryGetValue(bankKey, out var bank) && bank != null)
            {
                foreach (var fname in new[] { "_ambientBank", "_soundBank" })
                {
                    var fi = AccessTools.Field(type, fname);
                    if (fi != null && fi.FieldType.IsInstanceOfType(bank)) { fi.SetValue(comp, bank); break; }
                }
            }
            var clipName = row.Value<string>("_loopClipName");
            if (!string.IsNullOrEmpty(clipName))
            {
                var clip = FindClip(clipName);
                if (clip != null)
                {
                    var fi = AccessTools.Field(type, "_loopClip");
                    if (fi != null && fi.FieldType == typeof(AudioClip)) fi.SetValue(comp, clip);
                }
                else _bankClipMisses.Add(clipName);
            }
        }

        // -------------------------------------------- ambient authoring (interim tier)
        // the rip stripped the ambient governor scripts, leaving raw AudioSources
        // looping ungated at full volume (2026-08-11: MetalSqueaks authored at
        // _volume 0.15 / playOnAwake OFF played as a full-volume loop — the "rat
        // squeak"). until the full ambient-layer resurrection, apply the AUTHORED
        // params from the sidecar to every player GO and rebuild the one behavior
        // raw sources cant express: random re-trigger windows (_randomTimeRange).
        private static GameObject _ambientMarker;
        private static readonly string[] LoopPlayerClasses = { "LoopAmbientSoundPlayer", "SeasonLoopAmbientSoundPlayer" };
        private static readonly string[] ShotPlayerClasses =
            { "AmbientSoundPlayer", "OneShotAmbientSoundPlayer", "SeasonAmbientSoundPlayer", "WeatherRandomAmbientSoundPlayer" };

        public static void TryApplyAmbientAuthoring()
        {
            if (_ambientMarker != null) return;
            // superseded once the real layer is live: retail's own players own their
            // volumes and re-trigger timing from there on
            if (AmbientStaged) return;
            var sc = Sidecar();
            var sound = sc?["scenes"]?["Terminal_Sound"] as JObject;
            if (sound == null) return;
            var scene = SceneManager.GetSceneByName("Terminal_Sound");
            if (!scene.IsValid() || !scene.isLoaded) return;

            try
            {
                var goIndex = BuildGoIndex();
                int tuned = 0, stopped = 0, governed = 0;

                void Apply(string cls, bool isLoop)
                {
                    foreach (var row in Rows(sound, cls))
                    {
                        if (row["parse_error"] != null) continue;
                        var f = row["fields"] as JObject;
                        var t = FindGo(goIndex, row);
                        if (f == null || t == null) continue;

                        float vol = f.Value<float?>("_volume") ?? 1f;
                        float blend = f.Value<float?>("_spatialBlend") ?? 1f;
                        float minD = f.Value<float?>("_minDistance") ?? 1f;
                        float maxD = f.Value<float?>("_maxDistance") ?? 40f;
                        bool onAwake = (f.Value<int?>("_playOnAwake") ?? 0) != 0;
                        var rtr = f["_randomTimeRange"] as JObject;

                        var sources = t.GetComponents<AudioSource>();
                        if (sources.Length == 0) sources = t.GetComponentsInChildren<AudioSource>(true);
                        foreach (var src in sources)
                        {
                            if (src == null) continue;
                            src.volume = vol;
                            src.spatialBlend = blend;
                            src.minDistance = minD;
                            src.maxDistance = maxD;
                            if (isLoop)
                            {
                                src.loop = true;
                                if (!src.isPlaying && src.clip != null) src.Play();
                            }
                            else
                            {
                                src.loop = false;
                                if (!onAwake && src.isPlaying) { src.Stop(); stopped++; }
                                float rMin = rtr?.Value<float?>("x") ?? 0f, rMax = rtr?.Value<float?>("y") ?? 0f;
                                if (rMax > 0.5f && src.clip != null && t.GetComponent<AmbientGovernor>() == null)
                                {
                                    var gov = t.gameObject.AddComponent<AmbientGovernor>();
                                    gov.Src = src;
                                    gov.MinWait = Mathf.Max(rMin, 4f);
                                    gov.MaxWait = Mathf.Max(rMax, gov.MinWait + 1f);
                                    governed++;
                                }
                            }
                            tuned++;
                        }
                    }
                }

                foreach (var cls in LoopPlayerClasses) Apply(cls, true);
                foreach (var cls in ShotPlayerClasses) Apply(cls, false);

                _ambientMarker = new GameObject("Terminal_AmbientAuthored");
                SceneManager.MoveGameObjectToScene(_ambientMarker, scene);
                Plugin.Log.LogInfo($"[Acoustics] ambient authoring applied: {tuned} source(s) set to retail volumes/rolloffs, "
                    + $"{stopped} ungated loop(s) stopped, {governed} random re-trigger governor(s) attached");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Acoustics] ambient authoring failed: {e.Message}"); }
        }

        // the one behavior a raw AudioSource cant express: play a one-shot every
        // rand(min,max) seconds — retail's random ambient trigger, rebuilt small
        internal class AmbientGovernor : MonoBehaviour
        {
            internal AudioSource Src;
            internal float MinWait = 20f, MaxWait = 90f;
            private float _next;

            private void Start() => _next = Time.time + UnityEngine.Random.Range(MinWait, MaxWait);

            private void Update()
            {
                if (Time.time < _next) return;
                _next = Time.time + UnityEngine.Random.Range(MinWait, MaxWait);
                try { if (Src != null && Src.clip != null && !Src.isPlaying) Src.Play(); } catch { }
            }
        }

        // SELF-DRIVEN SPATIAL INIT — TarkovApplication only calls Initialize when the
        // singleton is already Instantiated at ITS point in the load sequence, and our
        // component is resurrected on scene load, so the call can be missed entirely
        // (2026-08-11 raid: component created, Initialize never ran, rooms never
        // tracked -> every ambient player ungated = "same zone audio everywhere").
        // drive it ourselves; the staging prefix still does the real work, and
        // Initialized guards against a double run.
        private static bool _spatialKick;
        private static System.Threading.Tasks.Task _spatialTask;
        private static bool _spatialVerdict;

        // Initialize is async and we don't await it — a fault inside would otherwise be
        // completely silent (and "did spatial audio actually come up?" is the one
        // question the log couldn't answer). report the verdict once.
        public static void TickSpatialVerdict()
        {
            if (_spatialVerdict || _spatialTask == null) return;
            if (!_spatialTask.IsCompleted) return;
            _spatialVerdict = true;
            if (_spatialTask.IsFaulted)
                Plugin.Log.LogError($"[Acoustics] SPATIAL INIT FAILED: {_spatialTask.Exception?.GetBaseException()}");
            else
                Plugin.Log.LogWarning($"[Acoustics] spatial init finished — SpatialAudioSystem.Initialized="
                    + $"{SpatialAudioSystem.Initialized} (rooms tracked = ambient gating live)");
        }

        public static void TickSpatialInit()
        {
            if (_spatialKick || !Plugin.SpatialAudio.Value) return;
            try
            {
                if (!MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated) return;
                var sys = MonoBehaviourSingleton<SpatialAudioSystem>.Instance;
                if (sys == null) return;
                if (SpatialAudioSystem.Initialized) { _spatialKick = true; return; }
                _spatialKick = true;
                Plugin.Log.LogInfo("[Acoustics] driving SpatialAudioSystem.Initialize ourselves (the loader never did)");
                // ISOLATE the call: other audio mods prefix Initialize and can return
                // false, which kills the original for everyone — an older ManimalIcebreaker
                // build skipped it unconditionally and silently cost terminal (and every
                // vanilla map) its spatial audio. suspend FOREIGN patches for exactly this
                // call and restore them after (icebreaker's own loot-firewall pattern), so
                // terminal never depends on another mod's build state.
                var target = AccessTools.Method(typeof(SpatialAudioSystem), "Initialize");
                var suspended = SuspendForeign(target);
                try { _spatialTask = sys.Initialize(default(System.Threading.CancellationToken), null); }
                finally { RestoreForeign(suspended); }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Acoustics] self-driven spatial init failed: {e.Message}");
            }
        }

        private sealed class Suspended
        {
            internal System.Reflection.MethodBase Target;
            internal HarmonyLib.Patch Patch;
            internal HarmonyPatchType Kind;
        }

        private static List<Suspended> SuspendForeign(System.Reflection.MethodBase target)
        {
            var outp = new List<Suspended>();
            if (target == null) return outp;
            try
            {
                var info = Harmony.GetPatchInfo(target);
                if (info == null) return outp;
                void Collect(IEnumerable<HarmonyLib.Patch> patches, HarmonyPatchType kind)
                {
                    foreach (var p in patches ?? Enumerable.Empty<HarmonyLib.Patch>())
                        if (p.owner != null && !p.owner.StartsWith(BuildInfo.ModGuid, StringComparison.OrdinalIgnoreCase))
                            outp.Add(new Suspended { Target = target, Patch = p, Kind = kind });
                }
                Collect(info.Prefixes, HarmonyPatchType.Prefix);
                Collect(info.Finalizers, HarmonyPatchType.Finalizer);
                var h = new Harmony(BuildInfo.ModGuid + ".spatialisolation");
                foreach (var s in outp) h.Unpatch(s.Target, s.Patch.PatchMethod);
                if (outp.Count > 0)
                    Plugin.Log.LogInfo($"[Acoustics] suspended {outp.Count} third-party patch(es) on Initialize "
                        + $"({string.Join(", ", outp.Select(s => s.Patch.owner).Distinct().ToArray())}) — restored right after");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Acoustics] patch suspend failed: {e.Message}"); }
            return outp;
        }

        private static void RestoreForeign(List<Suspended> suspended)
        {
            foreach (var s in suspended)
            {
                try
                {
                    var h = new Harmony(s.Patch.owner);
                    var hm = new HarmonyMethod(s.Patch.PatchMethod)
                    {
                        priority = s.Patch.priority,
                        before = s.Patch.before?.Length > 0 ? s.Patch.before : null,
                        after = s.Patch.after?.Length > 0 ? s.Patch.after : null,
                    };
                    h.Patch(s.Target,
                        prefix: s.Kind == HarmonyPatchType.Prefix ? hm : null,
                        finalizer: s.Kind == HarmonyPatchType.Finalizer ? hm : null);
                }
                catch { /* that mod's hook stays off for this session — better than a silent no-op raid */ }
            }
        }

        // ------------------------------------------------- environment self-drive
        // BSG's continuous env driver is one async polling task that dies silently on
        // the first exception from a half-spawned player. drive it ourselves — cheap
        // idempotent call (SetTriggerForPlayer early-outs when nothing changed).
        private static EnvironmentType _lastEnv = (EnvironmentType)(-1);
        private static int _driveTick;

        public static void DriveEnvironment()
        {
            var em = EnvironmentManager.Instance;
            if (em == null) return;
            var world = Comfort.Common.Singleton<EFT.GameWorld>.Instance;
            if (world == null) return;
            _driveTick++;

            var main = world.MainPlayer;
            if (main != null)
            {
                try { em.UpdateEnvironmentForPlayer(main); } catch { }
                if (em.Environment != _lastEnv)
                {
                    _lastEnv = em.Environment;
                    Plugin.Log.LogDebug($"[Acoustics] environment -> {_lastEnv}");
                }
            }

            // bots at lower cadence — their env feeds AI hearing, not the mixer
            if ((_driveTick & 3) == 0)
            {
                var players = world.RegisteredPlayers;
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    if (p == null || p.IsYourPlayer) continue;
                    try { em.UpdateEnvironmentForPlayer(p); } catch { }
                }
            }
        }

        // --------------------------------------------------------------- tier 3: rooms
        public static bool TryPrepareSpatialAudio(SpatialAudioSystem system)
        {
            if (_spatialMarker != null) return true; // already staged this raid

            var sc = Sidecar();
            var sound = sc?["scenes"]?["Terminal_Sound"] as JObject;
            if (sound == null) return false;

            try
            {
                if (!EnsureBakeFile(sc)) return false;

                _clipCache = null; // per-raid clip instances
                _roomTonesBound = 0;
                _roomToneMisses.Clear();

                var goIndex = BuildGoIndex();
                var comps = new Dictionary<long, Component>();
                var reactivate = new List<GameObject>();

                // pass 1 — components on deactivated GOs (Awake must see real values)
                CreateAll<AudioTriggerArea>(sound, "AudioTriggerArea", goIndex, comps, reactivate);
                CreateAll<SpatialAudioPortal>(sound, "SpatialAudioPortal", goIndex, comps, reactivate);
                CreateAll<UniversalTriggerSpatialAudioPortal>(sound, "UniversalTriggerSpatialAudioPortal", goIndex, comps, reactivate);
                CreateAll<SpatialAudioRoom>(sound, "SpatialAudioRoom", goIndex, comps, reactivate);

                // pass 2 — fields + cross-refs while everything still sleeps
                foreach (var row in Rows(sound, "AudioTriggerArea"))
                    if (Comp<AudioTriggerArea>(comps, row) is AudioTriggerArea area)
                        FillArea(area, row);
                foreach (var row in Rows(sound, "SpatialAudioPortal"))
                    if (Comp<SpatialAudioPortal>(comps, row) is SpatialAudioPortal p)
                        FillPortal(p, row, comps);
                foreach (var row in Rows(sound, "UniversalTriggerSpatialAudioPortal"))
                    if (Comp<UniversalTriggerSpatialAudioPortal>(comps, row) is UniversalTriggerSpatialAudioPortal up)
                        FillPortal(up, row, comps);
                foreach (var row in Rows(sound, "SpatialAudioRoom"))
                    if (Comp<SpatialAudioRoom>(comps, row) is SpatialAudioRoom r)
                        FillRoom(r, row, comps);

                // pass 3 — wake everything with wired state
                foreach (var go in reactivate) go.SetActive(true);

                if (_roomTonesBound > 0 || _roomToneMisses.Count > 0)
                    Plugin.Log.LogInfo($"[Acoustics] bound {_roomTonesBound} authored room tones" +
                        (_roomToneMisses.Count > 0
                            ? $"; missing from the bundle: {string.Join(", ", _roomToneMisses.Take(8).ToArray())}{(_roomToneMisses.Count > 8 ? "..." : "")}"
                            : ""));

                // the room tracker enumerates rooms ONLY through
                // SpatialAudioCrossSceneGroup.AllCrossGroups — it must exist on the
                // room-tree root AFTER our components (Awake self-collects children)
                var rootPath = Rows(sound, "SpatialAudioCrossSceneGroup").FirstOrDefault()?.Value<string>("go")
                               ?? "SpatialAudioSystem";
                if (goIndex.TryGetValue(rootPath, out var roots) && roots.Count > 0)
                {
                    var rootGo = roots[0].gameObject;
                    if (rootGo.GetComponent<SpatialAudioCrossSceneGroup>() == null)
                        rootGo.AddComponent<SpatialAudioCrossSceneGroup>();
                }
                else Plugin.Log.LogWarning($"[Acoustics] room-tree root '{rootPath}' not found — tracker will see no rooms");

                StampDoorIds(sound, comps);

                // per-location assets rebuilt from recovered values
                var infoFields = sc["assets"]?["location_info"]?["fields"] as JObject;
                var info = ScriptableObject.CreateInstance<SpatialAudioLocationInfo>();
                if (infoFields != null) FillFields(info, infoFields, null);
                AccessTools.Field(typeof(SpatialAudioSystem), "_locationInfo").SetValue(system, info);

                if (system.OcclusionSettings == null)
                {
                    var occ = ScriptableObject.CreateInstance<AudioOcclusionSettings>();
                    if (sc["assets"]?["occlusion_settings"]?["fields"] is JObject occFields)
                        FillFields(occ, occFields, null);
                    system.OcclusionSettings = occ;
                }
                if (system.poolsConfig == null)
                {
                    system.poolsConfig = new SpatialAudioPoolsConfig();
                    if (sound["SpatialAudioSystem"] is JArray sysRows && sysRows.FirstOrDefault()?["fields"]?["poolsConfig"] is JObject pc)
                        FillFields(system.poolsConfig, pc, null);
                }

                _spatialMarker = new GameObject("Terminal_AcousticsStaged");
                var soundScene = SceneManager.GetSceneByName("Terminal_Sound");
                if (soundScene.IsValid() && soundScene.isLoaded)
                    SceneManager.MoveGameObjectToScene(_spatialMarker, soundScene);
                Plugin.Log.LogInfo($"[Acoustics] SPATIAL AUDIO STAGED: {comps.Count} components rehydrated — letting BSG's Initialize run for real");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Acoustics] spatial audio staging failed (falling back to no-spatial): {e}");
                return false;
            }
        }

        private static bool EnsureBakeFile(JObject sc)
        {
            var rel = sc["assets"]?["location_info"]?["fields"]?.Value<string>("relativeBakeDataPath")
                      ?? "AudioBakeData\\terminal_sound.audiobakedata";
            var dest = SysIoPath.Combine(Application.streamingAssetsPath, rel.Replace('\\', SysIoPath.DirectorySeparatorChar));
            if (System.IO.File.Exists(dest)) return true;
            var src = SysIoPath.Combine(DataDir, SysIoPath.GetFileName(dest));
            if (!System.IO.File.Exists(src))
            {
                Plugin.Log.LogWarning($"[Acoustics] bake file missing: copy the 1.0 client's "
                    + $"StreamingAssets/AudioBakeData/{SysIoPath.GetFileName(dest)} into {DataDir} — spatial audio stays off until then");
                return false;
            }
            System.IO.Directory.CreateDirectory(SysIoPath.GetDirectoryName(dest) ?? ".");
            System.IO.File.Copy(src, dest);
            Plugin.Log.LogInfo($"[Acoustics] installed audio bake -> {dest}");
            return true;
        }

        // ------------------------------------------------------------- scene GO lookup
        private static Dictionary<string, List<Transform>> BuildGoIndex()
        {
            var index = new Dictionary<string, List<Transform>>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scn = SceneManager.GetSceneAt(i);
                if (!scn.isLoaded || !scn.name.StartsWith("Terminal")) continue;
                foreach (var root in scn.GetRootGameObjects())
                    Walk(root.transform, root.name, index);
            }
            return index;
        }

        private static void Walk(Transform t, string path, Dictionary<string, List<Transform>> index)
        {
            if (!index.TryGetValue(path, out var list)) index[path] = list = new List<Transform>(1);
            list.Add(t);
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                Walk(c, path + "/" + c.name, index);
            }
        }

        // GO paths are not unique in the Sound scene — disambiguate by local position
        private static Transform FindGo(Dictionary<string, List<Transform>> index, JToken row)
        {
            var path = row.Value<string>("go");
            if (path == null || !index.TryGetValue(path, out var list)) return null;
            if (list.Count == 1) return list[0];
            var pos = row["trs"]?["pos"];
            if (pos == null) return list[0];
            var want = V3(pos);
            Transform best = null;
            float bestD = float.MaxValue;
            foreach (var t in list)
            {
                float d = (t.localPosition - want).sqrMagnitude;
                if (d < bestD) { bestD = d; best = t; }
            }
            return best;
        }

        private static IEnumerable<JToken> Rows(JObject scene, string cls)
            => (scene[cls] as JArray) ?? Enumerable.Empty<JToken>();

        private static void CreateAll<T>(JObject scene, string cls, Dictionary<string, List<Transform>> goIndex,
            Dictionary<long, Component> comps, List<GameObject> reactivate) where T : Component
        {
            int missing = 0;
            foreach (var row in Rows(scene, cls))
            {
                var t = FindGo(goIndex, row);
                if (t == null) { missing++; continue; }
                var go = t.gameObject;
                if (go.activeSelf) { go.SetActive(false); reactivate.Add(go); }
                comps[row.Value<long>("path_id")] = go.AddComponent<T>();
            }
            if (missing > 0)
                Plugin.Log.LogWarning($"[Acoustics] {cls}: {missing} GOs not found in scene (bundle drift?)");
        }

        private static T Comp<T>(Dictionary<long, Component> comps, JToken row) where T : Component
            => comps.TryGetValue(row.Value<long>("path_id"), out var c) ? c as T : null;

        private static Component Ref(Dictionary<long, Component> comps, JToken refTok)
        {
            var id = (refTok as JObject)?.Value<long?>("ref");
            return id.HasValue && comps.TryGetValue(id.Value, out var c) ? c : null;
        }

        // ------------------------------------------------------------------- fillers
        private static void FillArea(AudioTriggerArea area, JToken row)
        {
            var f = row["fields"];
            AccessTools.Field(typeof(ServerRoomColliderArea), "areaCollider")
                ?.SetValue(area, area.GetComponent<BoxCollider>());
            var validate = f?.Value<int?>("_validatePlayerCenterInside");
            if (validate.HasValue)
                AccessTools.Field(typeof(ServerRoomColliderArea), "_validatePlayerCenterInside")
                    ?.SetValue(area, validate.Value != 0);
        }

        private static void FillPortal(BaseSpatialAudioPortal p, JToken row, Dictionary<long, Component> comps)
        {
            var f = row["fields"] as JObject;
            if (f == null) return;

            p.Depth = f.Value<float?>("Depth") ?? 2f;
            p.portalName = f.Value<string>("portalName") ?? p.gameObject.name;
            p.FitToGeometry = (f.Value<int?>("FitToGeometry") ?? 0) != 0;
            p.AutoRotate = (f.Value<int?>("AutoRotate") ?? 0) != 0;
            p.traversalMaxCost = f.Value<float?>("traversalMaxCost") ?? 0f;
            p.ToOutdoor = (f.Value<int?>("ToOutdoor") ?? 0) != 0;
            p.openFadeTime = f.Value<float?>("openFadeTime") ?? 0.1f;
            p.closeFadeTime = f.Value<float?>("closeFadeTime") ?? 0.1f;
            p.openEnvelope = Curve(f["openEnvelope"]) ?? p.openEnvelope;
            p.closeEnvelope = Curve(f["closeEnvelope"]) ?? p.closeEnvelope;
            p.portalCollider = p.GetComponent<BoxCollider>();

            AccessTools.Field(typeof(BaseSpatialAudioPortal), "_iD").SetValue(p, (short)(f.Value<int?>("_iD") ?? 0));
            var rooms = new List<SpatialAudioRoom>(2);
            foreach (var rr in (f["_connectedRooms"] as JArray) ?? new JArray())
                if (Ref(comps, rr) is SpatialAudioRoom room) rooms.Add(room);
            AccessTools.Field(typeof(BaseSpatialAudioPortal), "_connectedRooms").SetValue(p, rooms);

            // retail did NOT serialize portalType/state — C# defaults Opening/Open, and
            // SyncState later syncs door-bound portals to their door. everything else
            // stays OPEN (the icebreaker lesson: inferring Closed sealed the whole map
            // acoustically — "everything sounds 4 floors down")
            string doorId = f.Value<string>("DoorID");
            if (p is SpatialAudioPortal sp)
            {
                sp.DoorID = doorId ?? "";
                sp.portalType = BaseSpatialAudioPortal.PortalType.Opening;
                sp.state = BaseSpatialAudioPortal.PortalState.Open;
            }
            else if (p is UniversalTriggerSpatialAudioPortal utp)
            {
                utp.portalType = BaseSpatialAudioPortal.PortalType.Opening;
                utp.state = BaseSpatialAudioPortal.PortalState.Open;
                AccessTools.Field(typeof(UniversalTriggerSpatialAudioPortal), "_openTriggerID")
                    ?.SetValue(utp, f.Value<string>("_openTriggerID") ?? "");
                AccessTools.Field(typeof(UniversalTriggerSpatialAudioPortal), "_closeTriggerID")
                    ?.SetValue(utp, f.Value<string>("_closeTriggerID") ?? "");
            }
        }

        private static Dictionary<string, AudioClip> _clipCache;
        private static int _roomTonesBound;
        private static readonly HashSet<string> _roomToneMisses = new HashSet<string>();

        private static AudioClip FindClip(string name)
        {
            if (_clipCache == null)
            {
                _clipCache = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in Resources.FindObjectsOfTypeAll<AudioClip>())
                    if (c != null && !string.IsNullOrEmpty(c.name)) _clipCache[c.name] = c;
            }
            return _clipCache.TryGetValue(name, out var clip) ? clip : null;
        }

        private static void FillRoom(SpatialAudioRoom r, JToken row, Dictionary<long, Component> comps)
        {
            var f = row["fields"] as JObject;
            if (f == null) return;

            r.priority = f.Value<int?>("priority") ?? 0;
            r.WallOcclusion = f.Value<float?>("WallOcclusion") ?? 0.5f;
            r.FitToGeometry = (f.Value<int?>("FitToGeometry") ?? 0) != 0;
            r.OnlyWired = (f.Value<int?>("OnlyWired") ?? 1) != 0;
            AccessTools.Field(typeof(SpatialAudioRoom), "Outdoor").SetValue(r, (f.Value<int?>("Outdoor") ?? 0) != 0);
            AccessTools.Field(typeof(SpatialAudioRoom), "Isolated").SetValue(r, (f.Value<int?>("Isolated") ?? 0) != 0);
            AccessTools.Field(typeof(SpatialAudioRoom), "_type").SetValue(r, (EAudioRoomTypeMask)(f.Value<int?>("_type") ?? 0));
            AccessTools.Field(typeof(SpatialAudioRoom), "_iD").SetValue(r, (short)(f.Value<int?>("_iD") ?? -1));
            AccessTools.Field(typeof(SpatialAudioRoom), "_roomSize").SetValue(r, f.Value<float?>("_roomSize") ?? 0f);
            if (f["_bounds"] is JObject b)
                AccessTools.Field(typeof(SpatialAudioRoom), "_bounds")
                    .SetValue(r, new Bounds(V3(b["m_Center"]), V3(b["m_Extent"]) * 2f));

            r.Areas = new List<AudioTriggerArea>();
            foreach (var a in (f["Areas"] as JArray) ?? new JArray())
                if (Ref(comps, a) is AudioTriggerArea area) r.Areas.Add(area);

            r.roomConnections = new List<SpatialAudioRoom.RoomConnection>();
            foreach (var c in (f["roomConnections"] as JArray) ?? new JArray())
            {
                var conn = new SpatialAudioRoom.RoomConnection
                {
                    connectedRoom = Ref(comps, c["connectedRoom"]) as SpatialAudioRoom,
                };
                var portals = new List<BaseSpatialAudioPortal>();
                foreach (var pp in (c["connectingPortals"] as JArray) ?? new JArray())
                    if (Ref(comps, pp) is BaseSpatialAudioPortal bp) portals.Add(bp);
                conn.connectingPortals = portals.ToArray();
                if (conn.connectedRoom != null) r.roomConnections.Add(conn);
            }

            // ROOM TONES — retail hangs the indoor bed on the ROOM. the sidecar carries
            // the clip NAME; if the bundle carries the clip it binds, misses are logged
            // once as a shopping list for a future clip-restore pass.
            var amb = new RoomAmbientData();
            if (f["AmbientData"] is JObject ad)
            {
                FillFields(amb, ad, name => name != "RoomTone" && name != "SeasonSoundPreset");
                var toneName = ad.Value<string>("RoomToneName");
                if (!string.IsNullOrEmpty(toneName))
                {
                    var clip = FindClip(toneName);
                    if (clip != null) { amb.RoomTone = clip; _roomTonesBound++; }
                    else _roomToneMisses.Add(toneName);
                }
            }
            r.AmbientData = amb;
        }

        // portals bind to doors by WorldInteractiveObject.Id + position. terminal's
        // doors carry retail-authored ids (1R rebake), so renames should be ~0 — but
        // the binding itself is load-bearing: a door with no portal falls through to
        // generic occlusion ("muffled from a meter away").
        private static void StampDoorIds(JObject sound, Dictionary<long, Component> comps)
        {
            var doors = UnityEngine.Object.FindObjectsOfType<Door>();
            if (doors.Length == 0) return;

            var pairs = new List<(float d2, Door door, string doorId, string origId)>();
            var portalByRow = new List<(SpatialAudioPortal p, string doorId)>();
            foreach (var row in Rows(sound, "SpatialAudioPortal"))
            {
                var doorId = row["fields"]?.Value<string>("DoorID");
                if (string.IsNullOrEmpty(doorId)) continue;
                if (!(Comp<SpatialAudioPortal>(comps, row) is SpatialAudioPortal p)) continue;
                portalByRow.Add((p, doorId));
                foreach (var d in doors)
                {
                    float d2 = (d.transform.position - p.transform.position).sqrMagnitude;
                    if (d2 < 15f * 15f) pairs.Add((d2, d, doorId, d.Id));
                }
            }
            // machine-independent order (the fika door-sync lesson)
            pairs.Sort((a, b) =>
            {
                int c = a.d2.CompareTo(b.d2);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.doorId, b.doorId);
                return c != 0 ? c : string.CompareOrdinal(a.origId, b.origId);
            });

            var doneDoors = new HashSet<Door>();
            var doneIds = new HashSet<string>();
            int stamped = 0, renamed = 0;
            foreach (var (d2, door, doorId, _) in pairs)
            {
                if (doneDoors.Contains(door) || doneIds.Contains(doorId)) continue;
                doneDoors.Add(door);
                doneIds.Add(doorId);
                if (door.Id != doorId)
                {
                    if (++renamed <= 12)
                        Plugin.Log.LogDebug($"[Acoustics] door id RENAME '{door.Id}' -> '{doorId}' at {door.transform.position}");
                    door.Id = doorId;
                }
                stamped++;
            }
            if (renamed > 12) Plugin.Log.LogDebug($"[Acoustics] ...{renamed} renames total");
            Plugin.Log.LogDebug($"[Acoustics] stamped retail DoorIDs: {stamped} doors bound "
                + $"({portalByRow.Count - stamped} portal ids unmatched, {doors.Length - stamped} doors unbound)");
        }

        // ------------------------------------------------------- generic json -> object
        private static void FillFields(object target, JObject fields, Func<string, bool> fieldFilter)
        {
            var type = target.GetType();
            foreach (var prop in fields.Properties())
            {
                if (fieldFilter != null && !fieldFilter(prop.Name)) continue;
                var fi = AccessTools.Field(type, prop.Name);
                if (fi == null) continue;
                try
                {
                    var v = ConvertToken(prop.Value, fi.FieldType, fi.GetValue(target));
                    if (v != null || !fi.FieldType.IsValueType) fi.SetValue(target, v);
                }
                catch { /* field drifted — keep the class default */ }
            }
        }

        private static object ConvertToken(JToken tok, Type type, object existing)
        {
            if (tok == null || tok.Type == JTokenType.Null) return null;

            if (type == typeof(string)) return tok.Value<string>();
            if (type == typeof(bool)) return tok.Type == JTokenType.Boolean ? tok.Value<bool>() : tok.Value<int>() != 0;
            if (type == typeof(short)) return (short)tok.Value<int>();
            if (type.IsEnum) return Enum.ToObject(type, tok.Value<int>());
            if (type.IsPrimitive) return Convert.ChangeType(((JValue)tok).Value, type,
                System.Globalization.CultureInfo.InvariantCulture);

            if (tok is JObject o)
            {
                if (type == typeof(Vector3)) return V3(o);
                if (type == typeof(Vector2)) return new Vector2(o.Value<float>("x"), o.Value<float>("y"));
                if (type == typeof(Quaternion)) return Q4(o);
                if (type == typeof(Bounds)) return new Bounds(V3(o["m_Center"]), V3(o["m_Extent"]) * 2f);
                if (type == typeof(LayerMask)) return (LayerMask)(o.Value<int?>("m_Bits") ?? 0);
                if (o["ref"] != null) return null;
                var inst = existing ?? Activator.CreateInstance(type);
                FillFields(inst, o, null);
                return inst;
            }

            if (tok is JArray arr)
            {
                Type elem = type.IsArray ? type.GetElementType()
                    : type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? type.GetGenericArguments()[0]
                    : null;
                if (elem == null) return null;
                var items = new List<object>(arr.Count);
                foreach (var e in arr)
                {
                    var v = ConvertToken(e, elem, null);
                    if (v == null && elem.IsValueType) return null;
                    items.Add(v);
                }
                if (type.IsArray)
                {
                    var a = Array.CreateInstance(elem, items.Count);
                    for (int i = 0; i < items.Count; i++) a.SetValue(items[i], i);
                    return a;
                }
                var list = (System.Collections.IList)Activator.CreateInstance(type);
                foreach (var it in items) list.Add(it);
                return list;
            }
            return null;
        }

        private static AnimationCurve Curve(JToken tok)
        {
            var keys = tok?["m_Curve"] as JArray;
            if (keys == null || keys.Count == 0) return null;
            var kf = new Keyframe[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                var k = keys[i];
                kf[i] = new Keyframe(
                    k.Value<float?>("time") ?? 0f, k.Value<float?>("value") ?? 0f,
                    k.Value<float?>("inSlope") ?? 0f, k.Value<float?>("outSlope") ?? 0f);
            }
            return new AnimationCurve(kf);
        }

        private static Vector3 V3(JToken t)
        {
            if (t is JArray a) return new Vector3(a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>());
            return new Vector3(t.Value<float>("x"), t.Value<float>("y"), t.Value<float>("z"));
        }

        private static Quaternion Q4(JToken t)
        {
            if (t is JArray a) return new Quaternion(a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>(), a[3].Value<float>());
            return new Quaternion(t.Value<float>("x"), t.Value<float>("y"), t.Value<float>("z"), t.Value<float>("w"));
        }
    }
}
