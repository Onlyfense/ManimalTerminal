using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Manimal.Terminal
{
    // THE ARTILLERY BARRAGE (ArtilleryTriggers, level628): four authored trigger
    // boxes, each with a native TriggerZone (artillery_trigger_1..4) plus a
    // 1.0-only ArtilleryChangeTriggerHandler. 4.0 SHIPS the full shelling system
    // (ServerShellingControllerClass + client FX/audio — the Streets mortar event)
    // gated on ArtilleryMapsConfigs containing the location id, so:
    //  - InjectConfig (at location capture, BEFORE BaseLocalGame checks) plants a
    //    Terminal map config built from terminal_artillery.json — 4 shelling zones
    //    centered on the authored trigger boxes, guns as an offshore battery
    //  - TryStage resurrects the TriggerZones; entering one emits its trigger and
    //    we call StartImmediateShellingZone on that zone: 10s warning whistle,
    //    then the barrage walks the area you're in. run.
    internal static class TerminalArtillery
    {
        private static bool _staged;
        private static bool _injected;

        private static readonly HashSet<string> NativeSet = new HashSet<string> { "TriggerZone" };

        internal static void ResetForRaid()
        {
            _staged = false;
            // config injection persists across raids by design — the dict entry
            // survives and BaseLocalGame re-checks it every game creation
        }

        // must run BEFORE BaseLocalGame.method_4's ArtilleryMapsConfigs check —
        // called from the location-capture prefix, only for Terminal raids
        internal static void InjectConfig()
        {
            if (_injected || !Plugin.Artillery.Value) return;
            try
            {
                var path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                    "plugin-data", "terminal_artillery.json");
                if (!System.IO.File.Exists(path))
                {
                    Plugin.Log.LogWarning("[Artillery] terminal_artillery.json missing — no barrage");
                    _injected = true;
                    return;
                }
                var cfgJson = JObject.Parse(System.IO.File.ReadAllText(path));

                var backend = Singleton<BackendConfigSettingsClass>.Instance;
                var shelling = backend != null ? backend.ArtilleryShelling : null;
                if (shelling == null || shelling.ArtilleryMapsConfigs == null)
                {
                    Plugin.Log.LogWarning("[Artillery] backend ArtilleryShelling config absent — no barrage");
                    return; // retry next raid, backend may not be up yet
                }

                var cfgType = AccessTools.TypeByName("CommonAssets.Scripts.ArtilleryShelling.ArtilleryShellingMapConfiguration")
                    ?? AccessTools.TypeByName("ArtilleryShellingMapConfiguration");
                if (cfgType == null) { Plugin.Log.LogWarning("[Artillery] map-config type missing"); _injected = true; return; }
                object cfg = Activator.CreateInstance(cfgType);
                TerminalRigFill.FillObject(cfg, cfgJson, new Dictionary<string, Transform>());

                var dict = (System.Collections.IDictionary)shelling.ArtilleryMapsConfigs;
                dict["Terminal"] = cfg;
                _injected = true;
                Plugin.Log.LogInfo("[Artillery] Terminal shelling config injected — native controllers will arm at game start");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Artillery] config inject failed: {e}"); }
        }

        // NOBODY pumps ServerShellingController.OnUpdate in the offline game path
        // (proved 2026-08-18: zones tripped, countdown ran, TargetShellingOn set —
        // and the firing loop never executed). we are the pump.
        internal static void Pump()
        {
            if (!Plugin.Artillery.Value || !TerminalGate.On) return;
            try
            {
                var gw = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
                gw?.ServerShellingController?.OnUpdate();
            }
            catch (Exception e)
            {
                if (!_pumpWarned) { _pumpWarned = true; Plugin.Log.LogWarning($"[Artillery] pump threw (once): {e}"); }
            }
        }
        private static bool _pumpWarned;

        internal static void TryStage()
        {
            if (_staged || !Plugin.Artillery.Value) return;
            if (!TerminalGate.On) return;
            var gw = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            if (gw == null || gw.TriggersEmitter == null) return;

            var root = TerminalRigFill.FindRootNamed("ArtilleryTriggers");
            if (!root) return;

            try
            {
                var path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                    "plugin-data", "terminal_endgame.json");
                var sc = JObject.Parse(System.IO.File.ReadAllText(path));

                var index = new Dictionary<string, Transform>();
                TerminalRigFill.IndexTree(root, "ArtilleryTriggers", index);
                TerminalRigFill.StageRows(sc["rows"] as JArray, NativeSet, index, "Artillery");

                if (gw.ServerShellingController == null)
                    Plugin.Log.LogWarning("[Artillery] shelling controller NOT created — config injection missed the game-create window?");

                for (int i = 1; i <= 4; i++)
                {
                    var zoneId = i.ToString();
                    GClass3592.Instance.Subscribe($"artillery_trigger_{i}", new Action(() => OnZoneTripped(zoneId)));
                }

                _staged = true;
                Plugin.Log.LogInfo("[Artillery] ARTILLERY RESTORED — 4 trigger zones armed, barrage on entry");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Artillery] staging failed: {e}");
                _staged = true;
            }
        }

        private static void OnZoneTripped(string zoneId)
        {
            try
            {
                var ctrl = ServerShellingControllerClass.Instance;
                if (ctrl == null) { Plugin.Log.LogWarning("[Artillery] zone tripped but no shelling controller"); return; }
                ctrl.StartImmediateShellingZone(zoneId);
                Plugin.Log.LogInfo($"[Artillery] zone {zoneId} tripped — shelling called in");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Artillery] shelling start failed: {e}"); }
        }
    }
}
