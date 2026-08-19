using System.Collections.Generic;
using BepInEx.Logging;
using EFT;
using UnityEngine;

namespace Manimal.Terminal.Civilian
{
    // stuck-detector + recovery for the flee logic: vanilla AI has no vault/jump
    // decision node and hangs on geometry the navmesh didnt cut (doorframes,
    // furniture, stair corners). samples position every 0.5s while the mover is
    // actively moving; stalls escalate vault -> jump -> repath. only checked
    // while IsMoving — an idle bot naturally has zero movement delta and earlier
    // versions randomly jumped in place because of it.
    internal static class CivilianUnstickHelper
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Terminal.Civ");

        public enum Status
        {
            Moving,
            Attempted,
            NeedsRepath,
        }

        private sealed class StuckState
        {
            public Vector3 SamplePos;
            public float SampleAt;
            public float StalledSince = -1f;
            public float NextAttemptAt;
            public int FailedAttempts;
        }

        private static readonly Dictionary<BotOwner, StuckState> States = new Dictionary<BotOwner, StuckState>();

        private const float SampleInterval = 0.5f;
        private const float StuckSampleDistance = 0.5f;
        // 2s not 1s — brief direction changes near the destination tripped the
        // shorter timer as false positives
        private const float StuckTriggerSeconds = 2f;
        // close to the destination the arrival check owns final approach;
        // micro-oscillation there is not "stuck"
        private const float MinRemainingDistance = 4f;
        private const float AttemptCooldownSeconds = 2f;
        private const int FailedAttemptsBeforeRepath = 2;

        public static Status Check(BotOwner bot, Vector3 destination)
        {
            if (bot == null) return Status.Moving;
            var player = bot.GetPlayer;
            if (player == null) return Status.Moving;

            if (bot.Mover == null || !bot.Mover.IsMoving)
            {
                if (States.TryGetValue(bot, out var idleState)) ResetStall(idleState);
                return Status.Moving;
            }

            var now = Time.time;
            if (!States.TryGetValue(bot, out var state))
            {
                state = new StuckState
                {
                    SamplePos = bot.Position,
                    SampleAt = now,
                };
                States[bot] = state;
                return Status.Moving;
            }

            if (now - state.SampleAt < SampleInterval) return Status.Moving;

            var pos = bot.Position;
            var movedSinceSample = Vector3.Distance(pos, state.SamplePos);
            state.SamplePos = pos;
            state.SampleAt = now;

            var distToDest = Vector3.Distance(pos, destination);
            if (distToDest < MinRemainingDistance)
            {
                ResetStall(state);
                return Status.Moving;
            }

            if (movedSinceSample >= StuckSampleDistance)
            {
                ResetStall(state);
                return Status.Moving;
            }

            if (state.StalledSince < 0f) state.StalledSince = now;

            if (now - state.StalledSince < StuckTriggerSeconds) return Status.Moving;
            if (now < state.NextAttemptAt) return Status.Moving;

            state.FailedAttempts++;
            state.NextAttemptAt = now + AttemptCooldownSeconds;
            state.StalledSince = now;

            if (state.FailedAttempts >= FailedAttemptsBeforeRepath)
            {
                Log.LogInfo($"[Unstick] {bot.Profile?.Nickname} still stuck after {state.FailedAttempts} attempt(s); repathing");
                state.FailedAttempts = 0;
                return Status.NeedsRepath;
            }

            TryUnstick(bot, player);
            return Status.Attempted;
        }

        public static void Reset(BotOwner bot)
        {
            if (bot == null) return;
            States.Remove(bot);
        }

        public static void ClearAll() => States.Clear();

        private static void ResetStall(StuckState state)
        {
            state.StalledSince = -1f;
            state.FailedAttempts = 0;
        }

        // vault first (ledges, railings), fall back to a jump — both no-op when
        // the pose doesnt allow them, so ordering is harmless
        private static void TryUnstick(BotOwner bot, Player player)
        {
            var ctx = player.MovementContext;
            if (ctx != null && ctx.TryVaulting()) return;
            player.Jump();
        }
    }
}
