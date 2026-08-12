using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // the icebreaker bot-hardening set, ported wholesale (user call 2026-08-10: copy
    // everything except the synth/crew-specific pieces). every patch here is a scar
    // from an icebreaker raid autopsy; terminal shares the same 4.0 assembly and the
    // same backported-map failure modes. NOT ported: IceCrewJobs (their bigbrain idle
    // layer — fed by icebreaker's crew system, only needed under SAIN; our restored
    // retail patrols drive vanilla PatrollingData) and the synthesized cover/zone
    // generation (terminal has the retail bake).
    internal static class TerminalBotFixes
    {
        // CachePoints pre-caches 30 bots' navigation points — an exception here (a
        // GroupPoint member the fill left null, a manual-point surprise) would abort
        // BotsController.Init; degrade to lazy per-bot cache misses instead
        [HarmonyPatch(typeof(AICoversData), "CachePoints")]
        internal static class Patch_CoversCache
        {
            private static Exception Finalizer(Exception __exception)
            {
                if (__exception == null) return null;
                if (!TerminalGate.On) return __exception;
                Plugin.Log.LogWarning($"[BotFix] swallowed AICoversData.CachePoints: {__exception.Message}");
                return null;
            }
        }

        // bot door graph refresh over our rebuilt NavMeshDoorLinks — a single bad link
        // must not cost the raid
        [HarmonyPatch(typeof(BotDoorsController), "RefreshData")]
        internal static class Patch_BotDoorsRefresh
        {
            private static Exception Finalizer(Exception __exception)
            {
                if (__exception == null) return null;
                if (!TerminalGate.On) return __exception;
                Plugin.Log.LogWarning($"[BotFix] swallowed BotDoorsController.RefreshData: {__exception.Message}");
                return null;
            }
        }

        // bot hearing: dangling despawned-bot refs in the sound graph NRE inside
        // PlaySound and abort the CALLER's flow (player shots, door slams) — swallow,
        // rate-limited log
        [HarmonyPatch(typeof(BotEventHandler), nameof(BotEventHandler.PlaySound))]
        internal static class Patch_PlaySoundAirbag
        {
            private static float _lastLog;

            private static Exception Finalizer(Exception __exception)
            {
                if (__exception == null) return null;
                if (!TerminalGate.On) return __exception;
                if (Time.unscaledTime - _lastLog > 30f)
                {
                    _lastLog = Time.unscaledTime;
                    Plugin.Log.LogWarning($"[BotFix] swallowed a bot-hearing exception (dangling despawned-bot ref) — player actions protected. inner: {__exception.Message}");
                }
                return null;
            }
        }

        // opening a door emits state-change triggers via a quest/event singleton that
        // is a dead shell on a backported map — every door interaction NREs AFTER the
        // swing. our map has no quest triggers to fire; vanilla maps keep the exception
        // (masking theirs would silently break quests)
        [HarmonyPatch(typeof(WorldInteractiveObject), "method_3")]
        internal static class Patch_DoorTriggerEmit
        {
            private static Exception Finalizer(Exception __exception)
                => __exception == null || TerminalGate.On ? null : __exception;
        }

        // THE activation fix (icebreaker's hardest-won scar): BSG's method_10 aborts
        // the ENTIRE bot activation on the first throw (internal catch -> silent
        // ActiveFail statue with no log). run the mirrored step list ourselves: each
        // step individually guarded, failures logged BY NAME, activation continues —
        // one broken subsystem no longer statues the bot. mirrors the 4.0 method_10
        // exactly (order matters: BotState=Active lands mid-list where BSG set it).
        [HarmonyPatch(typeof(BotOwner), "method_10")]
        internal static class Patch_BotActivationStepwise
        {
            private static readonly MethodInfo _m2 = AccessTools.Method(typeof(BotOwner), "method_2");
            private static readonly MethodInfo _m11 = AccessTools.Method(typeof(BotOwner), "method_11");

            private static bool Prefix(BotOwner __instance)
            {
                if (!TerminalGate.On) return true; // vanilla maps: BSG's original behavior
                var b = __instance;
                int failed = 0;
                void Step(string name, Action a)
                {
                    try { a(); }
                    catch (Exception e)
                    {
                        failed++;
                        Plugin.Log.LogError($"[BotFix] '{b.name}' activation step {name} FAILED: {e}");
                    }
                }

                Step("VoxelesPersonalData", () => b.VoxelesPersonalData.Activate(b.BotsGroup.BotGame.BotsController.CoversData));
                Step("LookSensor", () => b.LookSensor.Activate());
                Step("Settings", () => b.Settings.Activate());
                Step("ExternalItemsController", () => b.ExternalItemsController.Activate());
                Step("ItemTaker", () => b.ItemTaker.Activate());
                Step("BewarePlantedMine", () => b.BewarePlantedMine.Activate());
                Step("EnemyChooser", () => b.EnemyChooser.Activate());
                Step("PlanDropItem", () => b.PlanDropItem.Activate());
                Step("MinesData", () => b.MinesData.Activate());
                Step("ItemDropper", () => b.ItemDropper.Activate());
                Step("SuppressStationary", () => b.SuppressStationary.Activate());
                Step("NavMeshCutterController", () => b.NavMeshCutterController.Activate());
                Step("BotFollower", () => b.BotFollower.Activate());
                Step("FriendlyTilt", () => b.FriendlyTilt.Activate());
                Step("RandomPlanItemDropper", () => b.RandomPlanItemDropper.Activate());
                Step("Tactic", () => b.Tactic.Activate());
                Step("EnemiesController", () => b.EnemiesController.Activate(b.BotsGroup.BotGame.BotsController.OnlineDependenceSettings.CanPersueAxeman));
                Step("HearingSensor", () => b.HearingSensor.Init());
                Step("LeaveData", () => b.LeaveData.Activate(b.BotsGroup.BotZone.Modifier.LeaveDist));
                Step("Receiver", () => b.Receiver.Init());
                Step("Mover", () => b.Mover.Activate());
                Step("BotTalk", () => b.BotTalk.Activate());
                Step("LoyaltyData", () => b.LoyaltyData.Activate());
                Step("AssaultDangerArea", () => b.AssaultDangerArea.Activate());
                Step("DangerArea", () => b.DangerArea.Activate());
                Step("BotPersonalStats", () => b.BotPersonalStats.Init(b, b.BotsGroup.BotZone.name));
                Step("StandBy.InitPoints", () => b.StandBy.InitPoints(b.BotsGroup.BotZone.Modifier.DistToActivate, b.BotsGroup.BotZone.Modifier.DistToSleep));
                Step("method_2", () => _m2.Invoke(b, null));
                Step("FlashGrenade", () => b.FlashGrenade.Activate());
                Step("PeaceHardAim", () => b.PeaceHardAim.Activate());
                Step("ShootData", () => b.ShootData.Activate());
                Step("PeaceLook", () => b.PeaceLook.Activate());
                Step("NearDoorData", () => b.NearDoorData.Activate());
                Step("AIData", () => b.AIData.Activate());
                Step("UnityEditorRunChecker", () => b.UnityEditorRunChecker.Activate());
                Step("NightVision", () => b.NightVision.Activate());
                Step("SearchData", () => b.SearchData.Activate());
                Step("Medecine", () => b.Medecine.Activate());
                b.BotState = EBotState.Active;
                Step("Memory", () => b.Memory.Activate());
                Step("SuppressShoot", () => b.SuppressShoot.Activate());
                Step("EatDrinkData", () => b.EatDrinkData.Activate());
                Step("SecondWeaponData", () => b.SecondWeaponData.Activate());
                Step("BotLay", () => b.BotLay.Activate());
                Step("SuppressGrenade", () => b.SuppressGrenade.Activate());
                Step("method_11", () => _m11.Invoke(b, null));
                Step("Brain", () => b.Brain.Activate());
                Step("PatrollingData", () => b.PatrollingData.Activate());

                if (failed > 0)
                    Plugin.Log.LogWarning($"[BotFix] '{b.name}' activated with {failed} failed step(s) — degraded but alive");
                return false; // we ran the whole list
            }
        }
    }
}
