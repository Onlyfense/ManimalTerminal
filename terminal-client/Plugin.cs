using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace Manimal.Terminal
{
    // ManimalTerminal client — backport of the retail 1.0 Terminal map into SPT 4.x.
    // skeleton: logs itself alive; systems get ported from ManimalIcebreaker one
    // phase at a time (see docs/MAP-BACKPORT-PLAYBOOK.md).
    [BepInPlugin(BuildInfo.ModGuid, "Manimal-Terminal", BuildInfo.Version)]
    //
    // HARD DEPENDENCIES — declare, dont trust filename load-order luck (lesson from
    // icebreaker). guids read off the loaded plugins / verified icebreaker list, NOT
    // guessed — a typo here makes bepinex silently refuse to load us at all.
    //   contentbackport + blackdiv supply the map's bosses/items; bigbrain must
    //   register its layer machinery before anything touches bot brains; morebots
    //   makes custom role enums parse. blackdiv's own declared deps (wtt.armory,
    //   csgas) chain transitively — no need to re-declare them here.
    [BepInDependency("com.wtt.commonlib", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.wtt.contentbackport", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("xyz.drakia.bigbrain", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.morebotsapi.tacticaltoaster", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.blackdiv.tacticaltoaster", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static Harmony HarmonyInstance;

        internal static ConfigEntry<float> LampIntensity;
        internal static ConfigEntry<float> AmbientIntensity;
        internal static ConfigEntry<bool> LampShadows;
        internal static ConfigEntry<float> LightCullDistance;
        internal static ConfigEntry<string> CamDonorSkip;
        internal static ConfigEntry<bool> DevMode;
        internal static ConfigEntry<bool> LensFlares;
        internal static ConfigEntry<bool> AttackCutscene;
        internal static ConfigEntry<float> AttackCutsceneDelay;
        internal static ConfigEntry<bool> AttackCutsceneSkippable;
        internal static ConfigEntry<bool> PcDriverEnabled;
        internal static ConfigEntry<float> RaidStartHour;
        internal static ConfigEntry<bool> RetailAIBake;
        internal static ConfigEntry<bool> InstantDoorInteract;
        internal static ConfigEntry<bool> HoldBotsForCutscene;
        internal static ConfigEntry<bool> EventWavesPush;
        internal static ConfigEntry<bool> RuafNeutral;
        internal static ConfigEntry<int> BdHangarSquad;
        internal static ConfigEntry<float> NvgAmbient;
        internal static ConfigEntry<bool> GearConfiscation;
        internal static ConfigEntry<bool> SpatialAudio;
        internal static ConfigEntry<bool> AmbientRetail;
        internal static ConfigEntry<float> DoorFoleyVolume;
        internal static ConfigEntry<BepInEx.Configuration.KeyboardShortcut> SoundProbeKey;
        internal static ConfigEntry<BepInEx.Configuration.KeyboardShortcut> LightProbeKey;
        internal static ConfigEntry<bool> SoundRig;
        internal static ConfigEntry<float> SoundRigVolume;
        internal static ConfigEntry<bool> SoundRigAlarm;
        internal static ConfigEntry<bool> InteriorCrossCull;
        internal static ConfigEntry<float> CrossCullDistance;

        private void Awake()
        {
            Log = Logger;
            HarmonyInstance = new Harmony(BuildInfo.ModGuid);

            // the rip loses baked lighting (retail lamps serialize at intensity 0 —
            // brightness lived in lightmaps we don't have), so we revive them realtime.
            // defaults are icebreaker's shipped values — retune per-map once in-raid.
            LampIntensity = Config.Bind("Terminal", "LampIntensity", 2.0f,
                new ConfigDescription("brightness of the revived lamp lights (0 = lights fully OFF — a big GPU win, emissives carry the look)",
                    new AcceptableValueRange<float>(0f, 12f)));
            AmbientIntensity = Config.Bind("Terminal", "AmbientIntensity", 0.8f,
                new ConfigDescription("flat ambient fill light — lifts shadowed areas out of black (no real bounce without a bake)",
                    new AcceptableValueRange<float>(0f, 3f)));
            LampShadows = Config.Bind("Terminal", "LampShadows", false,
                new ConfigDescription("let the revived lamps cast realtime shadows — much prettier, much heavier"));
            LightCullDistance = Config.Bind("Terminal", "LightCullDistance", 25f,
                new ConfigDescription("meters at which lamp lights finish fading to zero (live, lowering only — raising needs a raid restart). tightens bsg's native 50-80m fade window; lower = more fps + darker distance, 80 = authored retail look",
                    new AcceptableValueRange<float>(20f, 80f)));
            CamDonorSkip = Config.Bind("Terminal", "CamDonorSkip", "",
                new ConfigDescription("comma-separated component type names the donor graft must skip (bisecting a bad graft component)"));
            DevMode = Config.Bind("Terminal", "DevMode", false,
                new ConfigDescription("developer tooling: records the camera donor dump on vanilla raids. OFF for normal play"));
            LensFlares = Config.Bind("Terminal", "LensFlares", true,
                new ConfigDescription("rebuild the 3011 retail per-lamp lens flares (perf A/B lever)"));
            AttackCutscene = Config.Bind("Terminal", "AttackCutscene", true,
                new ConfigDescription("play the timed PortCutscene_01_Attack mid-raid"));
            AttackCutsceneDelay = Config.Bind("Terminal", "AttackCutsceneDelay", 45f,
                new ConfigDescription("seconds after the intro cutscene ends before the attack cutscene fires (45 = retail timing, measured off live footage)",
                    new AcceptableValueRange<float>(5f, 600f)));
            AttackCutsceneSkippable = Config.Bind("Terminal", "AttackCutsceneSkippable", true,
                new ConfigDescription("SPACE skips the attack cutscene (mid-raid; off = retail-faithful unskippable)"));
            InstantDoorInteract = Config.Bind("Terminal", "InstantDoorInteract", true,
                new ConfigDescription("open doors through the direct state path instead of the player animation (also the door-swing diagnostic — see TerminalInteractables)"));
            HoldBotsForCutscene = Config.Bind("Terminal", "HoldBotsForCutscene", true,
                new ConfigDescription("no bots until the attack cutscene has played — the port isnt at war before the attack"));
            DoorFoleyVolume = Config.Bind("Terminal", "DoorFoleyVolume", 1.0f,
                new ConfigDescription("door open/squeak foley volume multiplier (1.0 = authored levels)",
                    new AcceptableValueRange<float>(0f, 1f)));
            AmbientRetail = Config.Bind("Terminal", "AmbientRetail", true,
                new ConfigDescription("rebuild retail's authored ambient layer (44 sound banks + 1375 players/points/splines with their real volumes) instead of the hand-tuned approximation"));
            SpatialAudio = Config.Bind("Terminal", "SpatialAudio", true,
                new ConfigDescription("resurrect retail's spatial audio (104 rooms / 345 portals / occlusion bake) + indoor-outdoor environment layer. needs terminal_sound.audiobakedata in plugin-data/acoustics"));
            SoundProbeKey = Config.Bind("Terminal", "SoundProbeKey",
                new BepInEx.Configuration.KeyboardShortcut(UnityEngine.KeyCode.F11),
                new ConfigDescription("dump every audibly-playing source near the player to the log (the what-is-that-noise button)"));
            GearConfiscation = Config.Bind("Terminal", "GearConfiscation", true,
                new ConfigDescription("port security takes your headgear, weapons, backpack and carried items at raid start — locked in one random equipment cabinet (rig/armor/belt stay on, secured container untouched)"));
            NvgAmbient = Config.Bind("Terminal", "NvgAmbient", 2.5f,
                new ConfigDescription("night-vision ambient boost multiplier (x the flat ambient fill) — retail authors the NVG hemisphere at 0/black and relied on lightmaps we dont have",
                    new AcceptableValueRange<float>(0.5f, 8f)));
            BdHangarSquad = Config.Bind("Terminal", "BdHangarSquad", 4,
                new ConfigDescription("total black division holding the keycard hangar — the TB8 wave under-delivers past the zone's 2 born positions, the topper force-spawns the shortfall",
                    new AcceptableValueRange<int>(0, 8)));
            LightProbeKey = Config.Bind("Terminal", "LightProbeKey",
                new BepInEx.Configuration.KeyboardShortcut(UnityEngine.KeyCode.F10),
                new ConfigDescription("dump every lit light within 4m of the player to the log (the what-is-that-glow button)"));
            RuafNeutral = Config.Bind("Terminal", "RuafNeutral", true,
                new ConfigDescription("ruaf never add human enemies unless shot first — neutral until you draw blood"));
            EventWavesPush = Config.Bind("Terminal", "EventWavesPush", true,
                new ConfigDescription("tier-event waves storm the players (bigbrain hunt layer) instead of passively patrolling their zone"));
            TerminalCrewJobs.Register();
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += TerminalAcoustics.OnSceneLoaded;
            RetailAIBake = Config.Bind("Terminal", "RetailAIBake", true,
                new ConfigDescription("fill the AI holders with the retail bake (covers/voxels/patrols/mines) at raid start — off = empty holders, bots stand around"));
            RaidStartHour = Config.Bind("Terminal", "RaidStartHour", 22f,
                new ConfigDescription("anchor the raid clock to this hour at raid start (clock keeps ticking after; the terminal event is authored for night). -1 = keep natural raid time",
                    new AcceptableValueRange<float>(-1f, 23.99f)));
            SoundRig = Config.Bind("Terminal", "SoundRig", true,
                new ConfigDescription("replay the retail SOUND rig — phased ambience (speech/footsteps/far combat), sirens, and the distant firefight director (1.0 orchestration classes are husks in 4.0, this drives the surviving AudioSources)"));
            SoundRigVolume = Config.Bind("Terminal", "SoundRigVolume", 1f,
                new ConfigDescription("master multiplier on all sound-rig playback (live)",
                    new AcceptableValueRange<float>(0f, 2f)));
            SoundRigAlarm = Config.Bind("Terminal", "SoundRigAlarm", true,
                new ConfigDescription("loop the air-raid sirens after the attack cutscene"));
            PcDriverEnabled = Config.Bind("Terminal", "PcDriverEnabled", true,
                new ConfigDescription("occlusion culling from the .pcbake sidecars (live kill switch — flip off to isolate pop-in: pops that stop are stale bake data)"));
            InteriorCrossCull = Config.Bind("Terminal", "InteriorCrossCull", true,
                new ConfigDescription("cull interior volumes wholesale when the camera is outside them beyond CrossCullDistance (live)"));
            CrossCullDistance = Config.Bind("Terminal", "CrossCullDistance", 20f,
                new ConfigDescription("how close an out-of-volume interior group must be to still render (doorway/window sightlines) (live)",
                    new AcceptableValueRange<float>(10f, 80f)));

            // preload the self-hosted Perfect Culling runtime so sidecar volumes can be
            // built (and so any bundle-shipped PerfectCullingVolume binds at scene load —
            // unity resolves bundle MonoBehaviours by assembly+class name against loaded
            // assemblies). sits next to this dll; harmless no-op if absent.
            try
            {
                var pcPath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "PerfectCullingRuntime.dll");
                if (System.IO.File.Exists(pcPath))
                {
                    System.Reflection.Assembly.LoadFrom(pcPath);
                    Log.LogInfo("PerfectCullingRuntime.dll preloaded (terminal occlusion culling)");
                }
            }
            catch (System.Exception e) { Log.LogWarning($"PerfectCullingRuntime preload failed: {e.Message}"); }

            // BUNDLE HOST FIRST — icebreaker lesson (twice, 07-31): a bad patch that
            // throws in Awake must never take the bundle host down with it. the host
            // is load-bearing; patches are not.
            TerminalBundleHost.Init(HarmonyInstance);

            // trap from icebreaker: one AmbiguousMatchException kills a whole
            // PatchAll batch — attach per-class in isolated try/catch instead, so a
            // bad patch loses itself, not the map
            Patch(typeof(TerminalGate.Patch_CaptureLocationId));
            Patch(typeof(TerminalIntroCutscene.Patch_PlayAtRaidStart));
            Patch(typeof(TerminalAttackCutscene.Patch_ArmAttackTimer));
            Patch(typeof(TerminalShaderRebind.Patch_RebindAtRaidStart));
            Patch(typeof(TerminalSoundRig.Patch_AttachAtRaidStart));
            Patch(typeof(TerminalRaidClock.Patch_PinRaidClock));
            Patch(typeof(TerminalAIBake.Patch_RestoreCoversData));
            Patch(typeof(TerminalAIPlaces.Patch_BuildSpawnTriggers));
            Patch(typeof(TerminalBotFixes.Patch_CoversCache));
            Patch(typeof(TerminalBotFixes.Patch_BotDoorsRefresh));
            Patch(typeof(TerminalBotFixes.Patch_PlaySoundAirbag));
            Patch(typeof(TerminalBotFixes.Patch_DoorTriggerEmit));
            Patch(typeof(TerminalBotFixes.Patch_BotActivationStepwise));
            Patch(typeof(TerminalInteractables.Patch_HealInteractables));
            Patch(typeof(TerminalSpawnGate.Patch_ArmGate));
            Patch(typeof(TerminalSpawnGate.Patch_GateWaves));
            Patch(typeof(TerminalSpawnGate.Patch_GateBosses));
            Patch(typeof(TerminalSpawnGate.Patch_GateNonWaves));
            Patch(typeof(TerminalAudioFixes.Patch_SpatialAudioInitSkip));
            Patch(typeof(TerminalAudioFixes.Patch_InteractiveOcclusionUninit));
            Patch(typeof(TerminalAudioFixes.Patch_SourceOcclusionUninit));
            Patch(typeof(TerminalInteractables.Patch_DoorProbe));
            Patch(typeof(TerminalRuafNeutral.Patch_RuafNeutralToHumans));
            Patch(typeof(TerminalGearTax.Patch_GearTax));
            Patch(typeof(TerminalAudioFixes.Patch_DoorFoleyVolume));
            Patch(typeof(TerminalEscortFix.Patch_EscortsHonourIgnoreMaxBots));
            Patch(typeof(TerminalLights.Patch_LightsAtRaidStart));
            Patch(typeof(TerminalCameraDonor.Patch_DumpDonorCamera));
            Patch(typeof(TerminalCameraDonor.Patch_GraftDonorCamera));
            Patch(typeof(Patch_RejectShellCameraPrefab));
            Patch(typeof(Patch_GrenadeFlashPrismRef));
            Patch(typeof(Patch_EffectsControllerFrostbite));
            Patch(typeof(Patch_RainScreenOnCam2));
            Patch(typeof(Patch_NightVisionNeverSpams));
            Patch(typeof(Patch_EffectsControllerInit));
            Patch(typeof(TerminalCullingDriver.Patch_CaptureCamera));
            Patch(typeof(TerminalCullingDriver.Patch_AttachAtRaidStart));
            Patch(typeof(Patch_SpatialAudioInit));
            Patch(typeof(Patch_OcclusionWhenUninitialized));
            Patch(typeof(Patch_WindowBreakerPrewarm));
            Patch(typeof(Patch_SpawnPmcScan));
            Patch(typeof(Patch_EnsureStationaryController));
            Patch(typeof(Patch_StationaryInit));
            Patch(typeof(Patch_BotZonePruneMarkers));
            Patch(typeof(Patch_BotDoorsRefresh));
            Patch(typeof(Patch_BotsInitFirewall));
            Patch(typeof(Patch_GameStartFirewall));
            Patch(typeof(Patch_EnsureEnvBeforeAnyPlayerInit));
            try { Patch_NativeCullingGate.TryPatch(HarmonyInstance); }
            catch (System.Exception e) { Log.LogError($"native culling gate FAILED: {e}"); }

            // material ownership capture must beat every mod's runtime spawns — scene
            // load IS that moment. gate by scene NAME, never TerminalGate.On: transit
            // preloads scenes before raid creation, so a location-gated hook is blind
            // on the shoreline transit path (transit-gate-blindness trap).
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) =>
            {
                try
                {
                    if (scene.name != null && scene.name.StartsWith("Terminal", System.StringComparison.OrdinalIgnoreCase))
                    {
                        TerminalShaderRebind.CaptureSceneMaterials(scene);
                        // kill the live-but-broken ripped components (TOD/weather/
                        // ambient) before their per-frame NREs snowball
                        TerminalSceneScrub.Scrub(scene);
                    }
                }
                catch { }
            };

            Log.LogInfo($"[Manimal-Terminal] {BuildInfo.Version} loaded");
        }

        private void Patch(System.Type t)
        {
            try { HarmonyInstance.CreateClassProcessor(t).Patch(); }
            catch (System.Exception e) { Log.LogError($"patch {t.Name} FAILED: {e}"); }
        }
    }
}
