using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace Manimal.Terminal
{
    // HANGAR SQUAD TOPPER — icebreaker's deterministic-crew core, trimmed to one job.
    // the TB8 wave authors boss + 3 escorts into Zone1BD1HangarBD11, but the zone has
    // TWO born positions and the spawner silently drops what it cant place (2026-08-10
    // raid: 1 of 4 arrived; server pool had 100 blackDivAssault profiles, so placement
    // is the loss, not generation). icebreaker's answer: count what actually spawned,
    // force the remainder through BotSpawner.TryToSpawnInZoneInner — the call that
    // bypasses the scenario/limit machinery entirely.
    //
    // armed by the first GUARD BD_hold assignment (the boss landing is the proof the
    // wave fired); 20s later the box is counted and the shortfall spawned. the role
    // enum comes off the LIVE boss bot — no resolving the faction mod's custom int.
    internal class TerminalHangarCrew : MonoBehaviour
    {
        private static bool _armedThisRaid;
        private static bool _zoneAugmented;

        internal static void Arm()
        {
            if (_armedThisRaid || !Plugin.EventWavesPush.Value) return;
            _armedThisRaid = true;
            new GameObject("Terminal_HangarCrew").AddComponent<TerminalHangarCrew>();
        }

        internal static void ResetForRaid() { _armedThisRaid = false; _zoneAugmented = false; }

        // THE NATIVE FIX (2026-08-11, "they should spawn at the same time as the
        // initial guy"): the hangar zone ships TWO born positions with rad:35 sphere
        // colliders — a 4-bot group cant place together and the scatter radius reaches
        // out the door, so the spawner delivered the squad staggered over minutes.
        // clone the authored marker onto 4 navmesh points INSIDE the hold box with a
        // 3m radius, append to the zone's marker list and drop its lazy _spawnPoints
        // cache — the TB8 group spawn then finds four tight interior points and lands
        // whole. runs at BotsController.Init, long before the trigger can fire.
        internal static void AugmentHangarZone()
        {
            if (_zoneAugmented) return;
            try
            {
                var hold = TerminalCrewJobs.BdHoldBoundsPublic();
                if (hold == null) return;
                BotZone zone = null;
                foreach (var z in FindObjectsOfType<BotZone>())
                    if (z != null && z.name == "Zone1BD1HangarBD11") { zone = z; break; }
                if (zone?.SpawnPointMarkers == null || zone.SpawnPointMarkers.Count == 0) return;
                var template = zone.SpawnPointMarkers[0];
                if (template == null) return;

                var b = hold.Value;
                var spots = new[]
                {
                    b.center + new Vector3( b.extents.x * 0.5f, 0f,  b.extents.z * 0.5f),
                    b.center + new Vector3(-b.extents.x * 0.5f, 0f,  b.extents.z * 0.5f),
                    b.center + new Vector3( b.extents.x * 0.5f, 0f, -b.extents.z * 0.5f),
                    b.center + new Vector3(-b.extents.x * 0.5f, 0f, -b.extents.z * 0.5f),
                };
                int added = 0;
                foreach (var s in spots)
                {
                    if (!UnityEngine.AI.NavMesh.SamplePosition(s, out var hit, 6f, UnityEngine.AI.NavMesh.AllAreas)) continue;
                    var go = Instantiate(template.gameObject, hit.position, Quaternion.identity, template.transform.parent);
                    go.name = $"BP.Terminal_HangarInterior_{added}";
                    var m = go.GetComponent<EFT.Game.Spawning.SpawnPointMarker>();
                    if (m == null) { Destroy(go); continue; }
                    var scol = go.GetComponent<SphereCollider>();
                    if (scol != null) scol.radius = 3f; // authored rad:35 is the out-the-door scatter
                    m.BotZone = zone;
                    zone.SpawnPointMarkers.Add(m);
                    added++;
                }
                if (added > 0)
                {
                    HarmonyLib.AccessTools.Field(typeof(BotZone), "_spawnPoints")?.SetValue(zone, null);
                    _zoneAugmented = true;
                    Plugin.Log.LogInfo($"[HangarCrew] hangar zone augmented: +{added} interior spawn marker(s), 3m radius — the TB8 group can land whole");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[HangarCrew] zone augmentation failed: {e.Message}"); }
        }

        private void Start() => StartCoroutine(Run());

        private IEnumerator Run()
        {
            yield return new WaitForSeconds(15f);
            try { CountAndTop(); }
            catch (Exception e) { Plugin.Log.LogWarning($"[HangarCrew] top-up failed: {e.Message}"); }
        }

        private void CountAndTop()
        {
            var hold = TerminalCrewJobs.BdHoldBoundsPublic();
            if (hold == null) { Done("no BD_hold bounds"); return; }
            // same reach margin as the guard assignment — the zone's spawn points sit
            // at the hangar mouth just outside the box, and a guard mid-walk-in must
            // count as present or the topper over-delivers
            var bounds = hold.Value;
            bounds.Expand(new Vector3(30f, 12f, 30f));

            BotOwner exemplar = null;
            int present = 0;
            foreach (var b in FindObjectsOfType<BotOwner>())
            {
                if (b == null || b.IsDead) continue;
                var role = b.Profile?.Info?.Settings?.Role.ToString() ?? "";
                if (role.IndexOf("blackdiv", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!bounds.Contains(b.Position)) continue;
                present++;
                exemplar = b;
            }
            int want = Plugin.BdHangarSquad.Value;
            int need = want - present;
            if (need <= 0)
            {
                Done($"squad at {present}/{want} — no top-up");
                return;
            }
            // the boss dying before the count used to kill the top-up with it ("no
            // exemplar bot"), which is exactly the raid where the squad is missing —
            // fall back to the role CrewLayer recorded when it first saw a blackdiv
            if (exemplar == null && TerminalCrewJobs.LastBlackDivRole == null)
            {
                Done($"squad at {present}/{want} but no blackdiv role seen yet — no top-up");
                return;
            }

            BotZone zone = null;
            foreach (var z in FindObjectsOfType<BotZone>())
                if (z != null && z.name == "Zone1BD1HangarBD11") { zone = z; break; }
            if (zone == null) { Done("hangar BotZone not found"); return; }

            var role0 = exemplar != null
                ? exemplar.Profile.Info.Settings.Role
                : TerminalCrewJobs.LastBlackDivRole.Value;
            Plugin.Log.LogInfo($"[HangarCrew] hangar squad {present}/{want} — force-spawning {need}x {role0}");
            _ = TopUp(role0, zone, need, hold.Value);
        }

        private async Task TopUp(WildSpawnType role, BotZone zone, int count, Bounds holdTight)
        {
            try
            {
                var spawner = Singleton<IBotGame>.Instance?.BotsController?.BotSpawner;
                if (spawner == null) { Plugin.Log.LogWarning("[HangarCrew] no BotSpawner"); return; }
                // EXPLICIT pick points inside the hold box — the zone's born positions
                // carry rad:35, and a null pick list lets the spawner randomize within
                // that radius = out the hangar door (2026-08-10: all 3 deliveries
                // landed at the mouth). points inside the box pin the spawn to the
                // building; when none qualify, nearest-to-box-center beats random.
                var pts = (zone.SpawnPoints ?? Array.Empty<EFT.Game.Spawning.ISpawnPoint>())
                    .Where(p => p != null && holdTight.Contains(p.Position))
                    .ToList();
                if (pts.Count == 0)
                    pts = (zone.SpawnPoints ?? Array.Empty<EFT.Game.Spawning.ISpawnPoint>())
                        .Where(p => p != null)
                        .OrderBy(p => (p.Position - holdTight.center).sqrMagnitude)
                        .Take(2)
                        .ToList();
                Plugin.Log.LogDebug($"[HangarCrew] {pts.Count} pick point(s) for the hangar interior");

                var spawnParams = new BotSpawnParams { ShallBeGroup = new ShallBeGroupParams(false, false, 1) };
                for (int i = 0; i < count; i++)
                {
                    var profileData = new BotProfileDataClass(EPlayerSide.Savage, role, BotDifficulty.normal, 5f, spawnParams, false);
                    var data = await BotCreationDataClass.Create(profileData, spawner.BotCreator, 1, spawner);
                    if (data == null) { Plugin.Log.LogWarning($"[HangarCrew] profile creation failed ({i + 1}/{count})"); continue; }
                    var pick = pts.Count > 0
                        ? new List<EFT.Game.Spawning.ISpawnPoint> { pts[i % pts.Count] }
                        : null;
                    spawner.TryToSpawnInZoneInner(zone, data, 1, false, true, pick, true);
                }
                Plugin.Log.LogInfo($"[HangarCrew] top-up delivered — hangar guards report via GUARD BD_hold lines");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[HangarCrew] force spawn failed: {e.Message}"); }
            finally { Destroy(gameObject); }
        }

        private void Done(string why)
        {
            Plugin.Log.LogInfo($"[HangarCrew] {why}");
            Destroy(gameObject);
        }
    }
}
