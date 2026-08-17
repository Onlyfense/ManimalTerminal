using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // THE STAGE DIRECTOR — retail terminal's AIPlaceTerminalWavesController, rebuilt
    // from the hand-decoded level635 authoring (the class doesn't exist in 4.0; the
    // 1596-byte component decoded clean, 0 bytes left — see the playbook entry).
    //
    // what retail authors, verbatim from the decode:
    //  - 3 stages: stage 1 = the 14 Zone1 zones, stage 2 = 18 Zone2 zones, stage 3 =
    //    the finale (Zone2BDPort32 / Zone2VSRFExit33 / Zone2CivShip34).
    //  - a stage COMPLETES on attrition: alive <= 10% of its population (min 1)
    //    (StagesData: RemainPercent=0.10, MimimumCount=1 on every stage).
    //  - stage 1 completion RAISES THE T0 TRIGGER (UnblocksKeys: stage1 -> T0) — the
    //    entire Zone2 wave ladder (t=1577..2400) keys on T0, so retail arms Zone2 by
    //    wiping Zone1, with the walk-in T0 boxes as the parallel path. we only had
    //    the walk-in path until now.
    //  - survivors MIGRATE forward faction-matched (MigrateData, all stage-1):
    //    scavs -> Containers17/Warehouse16, BD -> Warehouse18/19, vsRF -> PassMiddle15.
    //    the enemies you didn't kill retreat and reinforce the next area.
    //
    // deliberately not ported: BlockOnFirstStage zone-blocking (our bots are pinned by
    //  patrol jobs and never roam cross-zone, so blocking buys nothing and the BotZone
    //  block API is unverified), and the RoleChecker's "Name777" event (below 2 live
    //  blackDivision -> raise; nothing observable consumes that name — likely dead
    //  editor authoring).
    internal static class TerminalStageDirector
    {
        // stage -> its zones (decoded StagesData; stage 2/3 tracked for logs/future)
        private static readonly string[] Stage1Zones =
        {
            "Zone1BD1HangarAmbush11","Zone1BD1HangarBD11","Zone1BD1PortAmbush1","Zone1BDGateAmbush13",
            "Zone1ScavEnterStorm4","Zone1ScavEnterStorm7","Zone1ScavHangarStorm14","Zone1ScavMiddleAmbush5",
            "Zone1ScavMiddleAmbush8","Zone1ScavPortStorm6","Zone1ScavStoreStorm9","Zone1VSRF1Spawn3",
            "Zone1VSRFSnipeRoofPort2","Zone1VSRFStoreAmbush10",
        };

        // decoded MigrateData (stage 1, faction-matched fallback routes)
        private static readonly Dictionary<string, string[]> Migrate = new Dictionary<string, string[]>
        {
            ["Zone1ScavMiddleAmbush5"] = new[] { "Zone2ScavContainers17" },
            ["Zone1ScavHangarStorm14"] = new[] { "Zone2ScavContainers17", "Zone2ScavsWarehouse16" },
            ["Zone1ScavEnterStorm4"] = new[] { "Zone2ScavContainers17" },
            ["Zone1ScavEnterStorm7"] = new[] { "Zone2ScavContainers17", "Zone2ScavsWarehouse16" },
            ["Zone1ScavStoreStorm9"] = new[] { "Zone2ScavContainers17", "Zone2ScavsWarehouse16" },
            ["Zone1ScavPortStorm6"] = new[] { "Zone2ScavContainers17", "Zone2ScavsWarehouse16" },
            ["Zone1ScavMiddleAmbush8"] = new[] { "Zone2ScavContainers17", "Zone2ScavsWarehouse16" },
            ["Zone1BDGateAmbush13"] = new[] { "Zone2BDWarehouse18", "Zone2BDWarehouse19" },
            ["Zone1BD1HangarBD11"] = new[] { "Zone2BDWarehouse18", "Zone2BDWarehouse19" },
            ["Zone1BD1PortAmbush1"] = new[] { "Zone2BDWarehouse18", "Zone2BDWarehouse19" },
            ["Zone1BD1HangarAmbush11"] = new[] { "Zone2BDWarehouse18", "Zone2BDWarehouse19" },
            ["Zone1VSRF1Spawn3"] = new[] { "Zone2VSRFPassMiddle15" },
            ["Zone1VSRFStoreAmbush10"] = new[] { "Zone2VSRFPassMiddle15" },
            ["Zone1VSRFSnipeRoofPort2"] = new[] { "Zone2VSRFPassMiddle15" },
        };

        private const float RemainPercent = 0.10f; // decoded StagesData
        private const int MinimumCount = 1;
        // completion can't fire before the stage has a real population — the decoded
        // component counts its authored wave roster; we count live sightings, so a
        // floor keeps "killed the 2 start-wave scavs at minute 1" from ending stage 1
        private const int MinSeenToComplete = 8;

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_Arm
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!TerminalGate.On || !Plugin.StageDirector.Value) return;
                new GameObject("Terminal_StageDirector").AddComponent<Host>();
            }
        }

        internal class Host : MonoBehaviour
        {
            // BotZone is a Component — no ?. on Unity types (code style)
            private static string ZoneNameOf(BotOwner b)
            {
                var g = b.BotsGroup;
                if (g == null) return "";
                var z = g.BotZone;
                return z ? z.name : "";
            }

            private readonly HashSet<string> _seenStage1 = new HashSet<string>();
            private float _next;
            private bool _stage1Done;

            private void Update()
            {
                if (Time.time < _next) return;
                _next = Time.time + 5f;
                if (_stage1Done) { Destroy(gameObject); return; }

                try
                {
                    var stage1 = new HashSet<string>(Stage1Zones);
                    var alive = new List<BotOwner>();
                    foreach (var b in FindObjectsOfType<BotOwner>())
                    {
                        if (!b || b.IsDead) continue;
                        var zone = ZoneNameOf(b);
                        if (!stage1.Contains(zone)) continue;
                        alive.Add(b);
                        var id = b.ProfileId;
                        if (id != null) _seenStage1.Add(id);
                    }

                    if (_seenStage1.Count < MinSeenToComplete) return;
                    int threshold = Mathf.Max(MinimumCount, Mathf.CeilToInt(_seenStage1.Count * RemainPercent));
                    if (alive.Count > threshold) return;

                    _stage1Done = true;
                    Plugin.Log.LogWarning($"[StageDirector] STAGE 1 CLEARED — {alive.Count} of {_seenStage1.Count} seen "
                        + $"bot(s) left in Zone1 (threshold {threshold}). raising T0: the Zone2 wave ladder arms, "
                        + "survivors fall back to their authored Zone2 posts");
                    RaiseT0();
                    MigrateSurvivors(alive);
                    Destroy(gameObject);
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[StageDirector] tick failed: {e.Message}"); }
            }

            // same path the walk-in T0 boxes use — the wave system can't tell the
            // difference, which is the point (retail fires the same trigger)
            private static void RaiseT0()
            {
                try
                {
                    Singleton<BotEventHandler>.Instance?.AnyEvent("T0");
                    TerminalCrewJobs.NoteEvent("T0 (stage-1 attrition)");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[StageDirector] T0 raise failed: {e.Message}"); }
            }

            // seed crew-job records directly (ByProfile lookup precedes the blackdiv
            // role gate, so this works for scavs and ruaf too — their brains carry our
            // layers): sprint to the authored fallback zone, then patrol it
            private static void MigrateSurvivors(List<BotOwner> alive)
            {
                var zonePos = new Dictionary<string, Vector3>();
                foreach (var z in FindObjectsOfType<BotZone>())
                {
                    if (!z || zonePos.ContainsKey(z.name)) continue;
                    var m = (z.SpawnPointMarkers != null && z.SpawnPointMarkers.Count > 0) ? z.SpawnPointMarkers[0] : null;
                    zonePos[z.name] = m ? m.Position : z.transform.position;
                }

                int moved = 0;
                foreach (var b in alive)
                {
                    try
                    {
                        var zone = ZoneNameOf(b);
                        if (!Migrate.TryGetValue(zone, out var dsts) || dsts.Length == 0) continue;
                        var dst = dsts[UnityEngine.Random.Range(0, dsts.Length)];
                        if (!zonePos.TryGetValue(dst, out var pos)) continue;
                        TerminalCrewJobs.ByProfile[b.ProfileId] = new TerminalCrewJobs.Rec
                        {
                            Job = TerminalCrewJobs.Job.Guard,
                            Zone = new Bounds(pos, new Vector3(30f, 10f, 30f)),
                            RushTo = pos,
                            RushUntil = Time.time + 180f, // it's a long walk from Zone1
                        };
                        moved++;
                        Plugin.Log.LogDebug($"[StageDirector] {b.name}: migrating {zone} -> {dst}");
                    }
                    catch { }
                }
                Plugin.Log.LogInfo($"[StageDirector] {moved} survivor(s) migrating to Zone2 fallback posts");
            }
        }
    }
}
