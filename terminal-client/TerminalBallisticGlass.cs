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
    // (the old parse-time PortBossPool lived here — replaced 2026-08-18 by
    // TerminalBossRoll + the five db rows, which carry the same live-dump escort
    // census: gluhar/sanitar/bully +3 at 5690, killa/tagilla solo at 5790)

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
                    int hit = 0, fence = 0;
                    foreach (var bc in UnityEngine.Object.FindObjectsOfType<BallisticCollider>())
                    {
                        if (!bc) continue;
                        // chainfence ships the same all-zero block (user 2026-08-18) —
                        // a chainlink fence you can't shoot through. every Chainfence-
                        // material collider is a fence; no name gate needed
                        if (bc.TypeOfMaterial == MaterialType.Chainfence)
                        {
                            bc.PenetrationChance = 1f;
                            bc.PenetrationLevel = 0f;
                            bc.RicochetChance = 0f;
                            fence++;
                            continue;
                        }
                        if (bc.TypeOfMaterial != MaterialType.Glass) continue;
                        if (bc.name.IndexOf("BALLISTIC", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        bc.PenetrationChance = 1f;
                        bc.PenetrationLevel = 0f;
                        bc.RicochetChance = 0f;
                        hit++;
                    }
                    Plugin.Log.LogWarning($"[Glass] {hit} ballistic-glass + {fence} chainfence collider(s) made penetrable "
                        + "(pen chance 1, level 0) — player and AI rounds pass through");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Glass] sweep failed: {e.Message}"); }
            }
        }
    }
}
