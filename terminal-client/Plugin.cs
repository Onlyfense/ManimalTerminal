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
        internal static ConfigEntry<bool> LampAuthored;
        internal static ConfigEntry<float> LampAuthoredScale;
        internal static ConfigEntry<float> AmbientIntensity;
        internal static ConfigEntry<bool> AmbientSkyLuminance;
        internal static ConfigEntry<bool> LampShadows;
        internal static ConfigEntry<float> LightCullDistance;
        internal static ConfigEntry<float> LodBiasClamp;
        internal static ConfigEntry<float> LodCullFloor;
        internal static ConfigEntry<float> LodCullNearFloor;
        internal static ConfigEntry<float> LodCullNearRadius;
        internal static ConfigEntry<float> LodCullNearRadiusIndoor;
        internal static ConfigEntry<float> LodCellSize;
        internal static ConfigEntry<float> LootCullRadius;
        internal static ConfigEntry<string> CamDonorSkip;
        internal static ConfigEntry<bool> DevMode;
        internal static ConfigEntry<bool> LensFlares;
        internal static ConfigEntry<bool> CutscenePlayerTop;
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
        internal static ConfigEntry<bool> RuafDefense;
        internal static ConfigEntry<bool> StageDirector;
        internal static ConfigEntry<int> BdHangarSquad;
        internal static ConfigEntry<float> NvgAmbient;
        internal static ConfigEntry<bool> GearConfiscation;
        internal static ConfigEntry<bool> SpatialAudio;
        internal static ConfigEntry<bool> AmbientRetail;
        internal static ConfigEntry<float> EnvironmentExposure;
        internal static ConfigEntry<bool> BallisticGlassPen;
        internal static ConfigEntry<float> GateAmbushTime;
        internal static ConfigEntry<bool> PortBossPool;
        internal static ConfigEntry<float> SkyHourOffset;
        internal static ConfigEntry<float> SkyNightStrength;
        internal static ConfigEntry<bool> WeatherStack;
        internal static ConfigEntry<bool> AmbientStencil;
        internal static ConfigEntry<bool> ForceWeather;
        internal static ConfigEntry<float> WeatherRain;
        internal static ConfigEntry<float> WeatherClouds;
        internal static ConfigEntry<float> WeatherFog;
        internal static ConfigEntry<float> WeatherWind;
        internal static ConfigEntry<float> WeatherThunder;
        internal static ConfigEntry<float> DoorFoleyVolume;
        internal static ConfigEntry<BepInEx.Configuration.KeyboardShortcut> SoundProbeKey;
        internal static ConfigEntry<BepInEx.Configuration.KeyboardShortcut> LightProbeKey;
        internal static ConfigEntry<BepInEx.Configuration.KeyboardShortcut> InteractProbeKey;
        internal static ConfigEntry<bool> SoundRig;
        internal static ConfigEntry<float> SoundRigVolume;
        internal static ConfigEntry<bool> SoundRigAlarm;
        internal static ConfigEntry<bool> InteriorCrossCull;
        internal static ConfigEntry<float> CrossCullDistance;

        private void Awake()
        {
            Log = Logger;
            HarmonyInstance = new Harmony(BuildInfo.ModGuid);

            // lamps serialize at intensity 0 because the runtime lamp SYSTEM drives
            // them (LampController/SceneLights) and its controllers rip dead — tarkov
            // has no baked lighting (user correction 2026-08-15; earlier "lightmaps"
            // comments were an unverified story). we revive the lights directly.
            // defaults are icebreaker's shipped values — retune per-map once in-raid.
            LampAuthored = Config.Bind("Terminal", "LampAuthored", true,
                new ConfigDescription("restore each lamp's RETAIL-authored intensity/color/range (7585 extracted — colored indicators, sodium spots, and ~3000 authored-dark broken fixtures that stay dark). off = the old flat LampIntensity for everything"));
            LampAuthoredScale = Config.Bind("Terminal", "LampAuthoredScale", 1.0f,
                new ConfigDescription("multiplier on the authored intensities (1.0 = exactly retail)",
                    new AcceptableValueRange<float>(0f, 4f)));
            LampIntensity = Config.Bind("Terminal", "LampIntensity", 2.0f,
                new ConfigDescription("brightness of the revived lamp lights (0 = lights fully OFF — a big GPU win, emissives carry the look)",
                    new AcceptableValueRange<float>(0f, 12f)));
            AmbientSkyLuminance = Config.Bind("Terminal", "AmbientSkyLuminance", true,
                new ConfigDescription("use the sky's LITERAL brightness for ambient (retail model — near-black at night, the authored lamps carry the scene). off = sky tint at a fixed playable brightness. flip live to A/B"));
            AmbientIntensity = Config.Bind("Terminal", "AmbientIntensity", 0.8f,
                new ConfigDescription("flat ambient fill light — lifts shadowed areas out of black (no real bounce without a bake)",
                    new AcceptableValueRange<float>(0f, 3f)));
            LampShadows = Config.Bind("Terminal", "LampShadows", false,
                new ConfigDescription("let the revived lamps cast realtime shadows — much prettier, much heavier"));
            // LOD CULL FLOOR SET, ported from icebreaker. defaults are deliberately LOOSER
            // than the ship's: terminal is a big open map, so the near bubble has to cover
            // real sightlines rather than a corridor, and the cells are coarser to keep the
            // re-tier sweep cheap over that area. all live except LodCellSize.
            LodBiasClamp = Config.Bind("Terminal", "LodBiasClamp", 1.0f,
                new ConfigDescription("caps unity's global LOD bias (LIVE). EFT's own slider floors at 2.0, which on a ripped map means props render at full detail far past where they matter. lower = more fps and earlier mesh swaps, higher = retail look. -1 = leave the game's value alone. NOTE this also shrinks every LOD CULL distance, which is what the floors below exist to compensate for",
                    new AcceptableValueRange<float>(-1f, 4f)));
            LodCullFloor = Config.Bind("Terminal", "LodCullFloor", 0.05f,
                new ConfigDescription("FAR-tier cull cap (LIVE): past LodCullNearRadius, props stop rendering below this screen fraction. higher = culls more = more fps but more visible popping at distance. -1 = retail heights",
                    new AcceptableValueRange<float>(-1f, 0.2f)));
            LodCullNearFloor = Config.Bind("Terminal", "LodCullNearFloor", 0.006f,
                new ConfigDescription("NEAR-tier cull cap (LIVE): inside the near radius, props only vanish below this screen fraction — the anti-dither guarantee for scenery around you. -1 = retail heights near you",
                    new AcceptableValueRange<float>(-1f, 0.05f)));
            LodCullNearRadius = Config.Bind("Terminal", "LodCullNearRadius", 80f,
                new ConfigDescription("meters around the camera that count as the near tier while OUTDOORS (LIVE). terminal's open sightlines need far more than a ship corridor, so this starts high — lower it for fps, raise it if scenery pops in ahead of you",
                    new AcceptableValueRange<float>(5f, 250f)));
            LodCullNearRadiusIndoor = Config.Bind("Terminal", "LodCullNearRadiusIndoor", 35f,
                new ConfigDescription("same, but while the camera is INDOORS (LIVE) — interiors have short sightlines, so a tighter bubble lets the far tier eat everything outside the room. drives off retail's EnvironmentManager/IndoorTriggers",
                    new AcceptableValueRange<float>(5f, 250f)));
            LodCellSize = Config.Bind("Terminal", "LodCellSize", 30f,
                new ConfigDescription("size in meters of the cells the map is bucketed into for tiering. bigger = fewer cells and a cheaper re-tier sweep, but coarser granularity at the radius edge. terminal defaults double the ship's 15m. NEEDS A RAID RESTART — cells are quantized around this at build",
                    new AcceptableValueRange<float>(10f, 100f)));
            LootCullRadius = Config.Bind("Terminal", "LootCullRadius", 40f,
                new ConfigDescription("meters at which loose LOOT stops rendering (LIVE). loot is exempted from the LOD cull entirely and culled by this radius instead, so it is visible at EVERY range inside it and simply gone outside — no fading, no dithering, no pop-in as you walk. LodBiasClamp shrinks every loot cull distance, which is what this compensates for. a hard cutoff is CHEAPER than hundreds of loot models rendering to subpixel size. 0 = off (loot follows the global LOD bias again)",
                    new AcceptableValueRange<float>(0f, 250f)));
            LightCullDistance = Config.Bind("Terminal", "LightCullDistance", 25f,
                new ConfigDescription("meters at which lamp lights finish fading to zero (live, lowering only — raising needs a raid restart). tightens bsg's native 50-80m fade window; lower = more fps + darker distance, 80 = authored retail look",
                    new AcceptableValueRange<float>(20f, 80f)));
            CamDonorSkip = Config.Bind("Terminal", "CamDonorSkip", "",
                new ConfigDescription("comma-separated component type names the donor graft must skip (bisecting a bad graft component)"));
            DevMode = Config.Bind("Terminal", "DevMode", false,
                new ConfigDescription("developer tooling: records the camera donor dump on vanilla raids. OFF for normal play"));
            LensFlares = Config.Bind("Terminal", "LensFlares", true,
                new ConfigDescription("rebuild the 3011 retail per-lamp lens flares (perf A/B lever)"));
            CutscenePlayerTop = Config.Bind("Terminal", "CutscenePlayerTop", true,
                new ConfigDescription("the intro cutscene's player actor wears YOUR pmc's equipped top instead of the generic packed torso (mesh + materials swapped onto the actor rig, bones remapped by name; aborts to the generic on any skeleton mismatch)"));
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
            // weather, set the Time&Weather-Changer way (WeatherDebug + the sliders).
            // retail terminal is a wet night port — rain by default.
            PortBossPool = Config.Bind("Terminal", "PortBossPool", true,
                new ConfigDescription("re-roll the container-berth (Zone2ScavPort29) boss each raid from retail's pool of 5 — gluhar/sanitar/reshala with 3 guards, killa/tagilla alone. our snapshot froze a single tagilla roll; live dumps prove the pool (gluhar x3, sanitar x2, tagilla x2, killa x1 across 8 raids)"));
            GateAmbushTime = Config.Bind("Terminal", "GateAmbushTime", 720f,
                new ConfigDescription("raid seconds until the Zone1BDGateAmbush13 pair spawns. retail authors 950 (15:50 in) — they beat you to the gate only if you fight slow; -1 keeps the authored timer",
                    new AcceptableValueRange<float>(-1f, 1800f)));
            BallisticGlassPen = Config.Bind("Terminal", "BallisticGlassPen", true,
                new ConfigDescription("make terminal's armored-glass panes shoot-through for player and AI (their colliders ship with penetration chance 0 = invisible bulletproof walls)"));
            SkyHourOffset = Config.Bind("Terminal", "SkyHourOffset", 2.5f,
                new ConfigDescription("hours added to the SKY's copy of the raid time (raid clock untouched). the TOD solver reads retail's backend time reference, not raw raid time — at 0 offset a 22:00 raid renders the sun ~7 degrees ABOVE the horizon (probe-verified). ~15 degrees of sun drop per hour; raise if you can still see sun glow at night",
                    new AcceptableValueRange<float>(-6f, 6f)));
            SkyNightStrength = Config.Bind("Terminal", "SkyNightStrength", 1f,
                new ConfigDescription("how hard to pin the sky to NIGHT between 21:00-06:00. the game's own ToDController (which normally darkens the atmosphere as the sun sets) lives on the scrubbed WeatherController, so without this the sky keeps the scene's authored daytime constants. 0 = leave the sky to the authored values",
                    new AcceptableValueRange<float>(0f, 1f)));
            AmbientStencil = Config.Bind("Terminal", "AmbientStencil", true,
                new ConfigDescription("restore retail's indoor/outdoor ambient masking: StencilShadow volumes + AnalyticSource portals feed the AmbientLight screen pass, so interiors stop receiving full sky ambient (the armory-cooking fix, done the retail way)"));
            WeatherStack = Config.Bind("Terminal", "WeatherStack", true,
                new ConfigDescription("rebuild retail's weather components (WeatherController/RainController/RainFall/Splash/Wind/Clouds) — without these the map CANNOT render rain at all. turn off if weather misbehaves"));
            ForceWeather = Config.Bind("Terminal", "ForceWeather", true,
                new ConfigDescription("set the raid's weather at start (retail terminal is a rainy night)"));
            WeatherRain = Config.Bind("Terminal", "WeatherRain", 0.6f,
                new ConfigDescription("rain amount", new AcceptableValueRange<float>(0f, 1f)));
            WeatherClouds = Config.Bind("Terminal", "WeatherClouds", 0.5f,
                new ConfigDescription("cloud density (-1 clear .. 1 overcast)", new AcceptableValueRange<float>(-1f, 1f)));
            WeatherFog = Config.Bind("Terminal", "WeatherFog", 0.012f,
                new ConfigDescription("fog density — small numbers, 0.004 is clear", new AcceptableValueRange<float>(0f, 0.1f)));
            WeatherWind = Config.Bind("Terminal", "WeatherWind", 0.3f,
                new ConfigDescription("wind magnitude", new AcceptableValueRange<float>(0f, 1f)));
            WeatherThunder = Config.Bind("Terminal", "WeatherThunder", 0.2f,
                new ConfigDescription("lightning/thunder probability", new AcceptableValueRange<float>(0f, 1f)));
            EnvironmentExposure = Config.Bind("Terminal", "EnvironmentExposure", 0f,
                new ConfigDescription("how much of retail's indoor/outdoor camera EXPOSURE the environment layer applies. 0 = none (our night stays night — retail's offsets assume baked lighting we don't have), 1 = retail's authored values",
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
            InteractProbeKey = Config.Bind("Terminal", "InteractProbeKey",
                new BepInEx.Configuration.KeyboardShortcut(UnityEngine.KeyCode.F9),
                new ConfigDescription("dump the full interaction state of every interactive object within 6m (the why-cant-i-open-this-door button)"));
            LightProbeKey = Config.Bind("Terminal", "LightProbeKey",
                new BepInEx.Configuration.KeyboardShortcut(UnityEngine.KeyCode.F10),
                new ConfigDescription("dump every lit light within 4m of the player to the log (the what-is-that-glow button)"));
            RuafNeutral = Config.Bind("Terminal", "RuafNeutral", true,
                new ConfigDescription("ruaf never add human enemies unless shot first — neutral until you draw blood"));
            StageDirector = Config.Bind("Terminal", "StageDirector", true,
                new ConfigDescription("retail AIPlaceTerminalWavesController port: clearing ~90% of Zone1 raises the T0 trigger (arming the Zone2 wave ladder without the walk-in boxes) and surviving Zone1 bots retreat to their authored Zone2 fallback zones, faction-matched"));
            RuafDefense = Config.Bind("Terminal", "RuafDefense", true,
                new ConfigDescription("retail VSRFDefence port: when ruaf lose sight of their enemy mid-fight they collapse onto cover near their boss (30m tether) and take heal breaks, instead of scattering or blind-pursuing. visible-enemy combat is untouched"));
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
            Patch(typeof(TerminalAIBake.Patch_RestoreCoversData));
            Patch(typeof(TerminalAIPlaces.Patch_BuildSpawnTriggers));
            Patch(typeof(TerminalBotFixes.Patch_CoversCache));
            Patch(typeof(TerminalBotFixes.Patch_BotDoorsRefresh));
            Patch(typeof(TerminalBotFixes.Patch_PlaySoundAirbag));
            Patch(typeof(TerminalBotFixes.Patch_DoorTriggerEmit));
            Patch(typeof(TerminalBotFixes.Patch_BotActivationStepwise));
            Patch(typeof(TerminalBotFixes.Patch_PatrolSubPoints));
            Patch(typeof(TerminalBotFixes.Patch_GetSubPointEmptyGuard));
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
            Patch(typeof(TerminalTimeWeather.Patch_Arm));
            Patch(typeof(TerminalBallisticGlass.Patch_Sweep));
            Patch(typeof(TerminalGateAmbushTime.Patch_Retime));
            Patch(typeof(TerminalStageDirector.Patch_Arm));
            Patch(typeof(TerminalPortBossPool.Patch_Roll));
            Patch(typeof(TerminalAudioFixes.Patch_DoorFoleyVolume));
            Patch(typeof(TerminalEscortFix.Patch_EscortsHonourIgnoreMaxBots));
            Patch(typeof(TerminalLights.Patch_LightsAtRaidStart));
            Patch(typeof(TerminalLights.Patch_LampsDead));
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
            // LockableDoors throws inside GetAvailableActions on our doors, which takes
            // the whole action list down — every door reads as "fake". suppressed here,
            // untouched on every other map (icebreaker shim, ported)
            try { TerminalLockableDoorsOff.TryPatch(HarmonyInstance); }
            catch (System.Exception e) { Log.LogWarning($"lockable-doors shim failed: {e}"); }

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
