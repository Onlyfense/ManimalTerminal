using System;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // GATE AMBUSH TIMER (user 2026-08-15: "the 2 guys look like theyre still not
    // spawning before i arrive... maybe im arriving too fast?"). exactly right — the
    // live dump authors the Zone1BDGateAmbush13 pair (BD + 1 escort) at Time=950s:
    // a hard 15:50 clock, no trigger. retail expects you to still be fighting through
    // the armory then; a fast player beats the ambush to its own post. rewrite the
    // wave's Time at parse (before any timer arms). keyed on the zone name — it only
    // exists on terminal, so no map gate needed (the location id may not even be set
    // yet when waves parse).
    // PORT BOSS POOL (user 2026-08-15, verified against 8 live match dumps): the
    // Zone2ScavPort29 T4 boss wave is NOT always tagilla — live rerolls it per raid
    // from a pool. observed across the dumps: gluhar x3 (followerGluharSecurity x3,
    // t=5690), sanitar x2 (followerSanitar x3, 5690), tagilla x2 / killa x1 (no
    // escorts, 5790; the escort-type field carries junk when amount=0). the wiki adds
    // reshala (3 guards) — never rolled in our captures; vanilla bully squad used.
    // (the 1.0 'bossBullyBlackDiv' strings are NOT a terminal reshala variant — per
    // the user, that's reshala's brain cloned onto the BD faction's bosses.) our
    // snapshot base.json froze one roll (tagilla); this prefix re-rolls at wave
    // parse, BEFORE BossName is parsed into BossType.
    internal static class TerminalPortBossPool
    {
        private struct Entry
        {
            public string Boss, Escort;
            public int Amount;
            public float Time;
        }

        private static readonly Entry[] Pool =
        {
            new Entry { Boss = "bossGluhar", Escort = "followerGluharSecurity", Amount = 3, Time = 5690f },
            new Entry { Boss = "bossKilla", Escort = "followerGluharSecurity", Amount = 0, Time = 5790f },
            new Entry { Boss = "bossBully", Escort = "followerBully", Amount = 3, Time = 5690f },
            new Entry { Boss = "bossSanitar", Escort = "followerSanitar", Amount = 3, Time = 5690f },
            new Entry { Boss = "bossTagilla", Escort = "followerGluharSecurity", Amount = 0, Time = 5790f },
        };

        private static Entry? _pick;

        internal static void ResetForRaid() => _pick = null;

        [HarmonyPatch(typeof(BossLocationSpawn), nameof(BossLocationSpawn.ParseMainTypesTypes))]
        internal static class Patch_Roll
        {
            [HarmonyPrefix]
            private static void Prefix(BossLocationSpawn __instance)
            {
                try
                {
                    if (!Plugin.PortBossPool.Value) return;
                    if (__instance.BossZone != "Zone2ScavPort29") return;
                    if (!(__instance.BossName ?? "").StartsWith("boss")) return;

                    // roll once per raid, but RE-APPLY the same pick on every parse —
                    // ParseMainTypesTypes can run more than once and the per-raid reset
                    // order isn't guaranteed; idempotent beats roll-once-and-hope
                    if (_pick == null)
                    {
                        _pick = Pool[UnityEngine.Random.Range(0, Pool.Length)];
                        Plugin.Log.LogInfo($"[PortBoss] container-berth boss rolled: {_pick.Value.Boss} "
                            + $"+ {_pick.Value.Amount}x {(_pick.Value.Amount > 0 ? _pick.Value.Escort : "(no escort)")} (T4 wave, retail pool of 5)");
                    }
                    var pick = _pick.Value;
                    __instance.BossName = pick.Boss;
                    // amount-0 waves still need a PARSEABLE escort type — Init parses it
                    // unconditionally (live carries junk like 'bossKilla' there; any
                    // valid role works since nothing spawns)
                    __instance.BossEscortType = pick.Escort;
                    __instance.BossEscortAmount = pick.Amount.ToString();
                    __instance.Time = pick.Time;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[PortBoss] roll failed: {e.Message}"); }
            }
        }
    }

    internal static class TerminalGateAmbushTime
    {
        [HarmonyPatch(typeof(BossLocationSpawn), nameof(BossLocationSpawn.ParseMainTypesTypes))]
        internal static class Patch_Retime
        {
            [HarmonyPostfix]
            private static void Postfix(BossLocationSpawn __instance)
            {
                try
                {
                    if (__instance.BossZone != "Zone1BDGateAmbush13") return;
                    float want = Plugin.GateAmbushTime.Value;
                    if (want < 0f || Mathf.Approximately(want, __instance.Time)) return;
                    Plugin.Log.LogWarning($"[GateAmbush] Zone1BDGateAmbush13 wave retimed {__instance.Time:0}s -> {want:0}s "
                        + "(authored 950 assumes a slow fight through the armory)");
                    __instance.Time = want;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[GateAmbush] retime failed: {e.Message}"); }
            }
        }
    }

    // SHOOT-THROUGH BALLISTIC GLASS (user 2026-08-15). terminal's armored-glass panes
    // ship with an all-zero BallisticCollider (pen/ricochet/fragment chances all 0) —
    // by the calculator's gate (BallisticCollider.cs:99, verified in the 4.0 decompile:
    //   PenetrationChance >= eps && shot.PenetrationPower > PenetrationLevel
    //   && shot.PenetrationChance + this.PenetrationChance > random(0..1))
    // a zero PenetrationChance can never pass, so the panes are bullet-proof walls for
    // player and AI alike. sweep: PenetrationChance=1 + PenetrationLevel=0 makes the
    // gate true for EVERY round; ricochet zeroed so rounds don't bounce instead.
    //
    // scope: TypeOfMaterial Glass with 'BALLISTIC' in the GO name — regular windows are
    // GlassShatter and already break like windows; they're left alone.
    internal static class TerminalBallisticGlass
    {
        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_Sweep
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!TerminalGate.On || !Plugin.BallisticGlassPen.Value) return;
                try
                {
                    int hit = 0;
                    foreach (var bc in UnityEngine.Object.FindObjectsOfType<BallisticCollider>())
                    {
                        if (!bc) continue;
                        if (bc.TypeOfMaterial != MaterialType.Glass) continue;
                        if (bc.name.IndexOf("BALLISTIC", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        bc.PenetrationChance = 1f;
                        bc.PenetrationLevel = 0f;
                        bc.RicochetChance = 0f;
                        hit++;
                    }
                    Plugin.Log.LogWarning($"[Glass] {hit} ballistic-glass collider(s) made penetrable (pen chance 1, level 0) — "
                        + "player and AI rounds pass through");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Glass] sweep failed: {e.Message}"); }
            }
        }
    }
}
