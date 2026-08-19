using BepInEx.Logging;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;

namespace Manimal.Terminal.Civilian.Behavior
{
    // activates when an armed threat is in range or gunfire was heard; hands
    // per-tick control to CivilianFleeLogic. priority sits above the vanilla
    // combat layers so flee always wins.
    internal sealed class CivilianFleeLayer : CustomLayer
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Terminal.Civ");

        public CivilianFleeLayer(BotOwner botOwner, int priority) : base(botOwner, priority) { }

        public override string GetName() => "Terminal.CivilianFleeLayer";

        private bool _wasActiveLastFrame;

        public override bool IsActive()
        {
            if (BotOwner == null || !BotOwner.GetPlayer.HealthController.IsAlive) return false;

            var state = CivilianFleeState.Get(BotOwner);

            // cowering: crouched in cover, intentionally blind to threats until
            // the full cower duration expires
            if (state.CoweringUntil > Time.time)
            {
                LogTransition(true, 0f);
                return true;
            }

            // cower just ended — clean flags for the next cycle
            if (state.InCover && state.CoweringUntil > 0f && state.CoweringUntil <= Time.time)
            {
                state.InCover = false;
                state.CoweringUntil = 0f;
                LogTransition(false, 0f);
                return false;
            }

            var threat = CivilianThreatScanner.FindNearestThreat(BotOwner, CivilianConstants.ThreatDetectionRadius);
            bool active;
            if (threat.HasThreat)
            {
                state.LastThreatSeenAt = Time.time;
                // new threat cancels any in-progress cover arrival
                state.InCover = false;
                active = true;
            }
            else
            {
                // stay committed to the run for the memory window
                active = Time.time - state.LastThreatSeenAt < CivilianConstants.ThreatMemorySeconds;
            }

            LogTransition(active, threat.Distance);
            return active;
        }

        private void LogTransition(bool active, float dist)
        {
            if (active != _wasActiveLastFrame)
            {
                Log.LogInfo($"Flee {(active ? "ENTER" : "EXIT")} on {BotOwner.Profile?.Nickname ?? "?"} (dist={dist:F1})");
                _wasActiveLastFrame = active;
            }
        }

        public override Action GetNextAction() =>
            new Action(typeof(CivilianFleeLogic), "civilian-flee");

        public override bool IsCurrentActionEnding() => !IsActive();
    }
}
