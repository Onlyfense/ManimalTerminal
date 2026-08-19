using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace Manimal.Terminal.Civilian.Behavior
{
    // always-active baseline — replaces the assault brain's combat layers so
    // civilians never shoot anyone. flee (priority 65) sits above and takes over
    // when a threat appears. the idle action is a gentle home-tethered wander.
    internal sealed class CivilianPassiveLayer : CustomLayer
    {
        public CivilianPassiveLayer(BotOwner botOwner, int priority) : base(botOwner, priority) { }

        public override string GetName() => "Terminal.CivilianPassiveLayer";

        private bool _loggedOnce;

        public override bool IsActive()
        {
            if (BotOwner == null || BotOwner.GetPlayer == null || !BotOwner.GetPlayer.HealthController.IsAlive)
                return false;

            if (!_loggedOnce)
            {
                _loggedOnce = true;
                var brainName = BotOwner.Brain?.BaseBrain?.ShortName() ?? "<null>";
                BepInEx.Logging.Logger.CreateLogSource("Terminal.Civ")
                    .LogInfo($"Passive attached to {BotOwner.Profile?.Nickname} brain='{brainName}'");
            }

            // passive is always active, so this catches every bot exactly once
            CivilianFleeState.EnsureHomeInitialized(BotOwner);
            return true;
        }

        public override Action GetNextAction() =>
            new Action(typeof(CivilianIdleLogic), "civilian-idle");

        public override bool IsCurrentActionEnding() => false;
    }

    internal sealed class CivilianIdleLogic : CustomLogic
    {
        private const float MinPauseSeconds = 6f;
        private const float MaxPauseSeconds = 15f;
        private const float WanderMinDistance = 3f;
        private const float WanderMaxDistance = 8f;
        private const float ArrivalDistance = 1.75f;

        private Vector3 _target;
        private bool _hasTarget;
        private float _nextMoveAt;
        private readonly System.Random _rng = new System.Random();

        public CivilianIdleLogic(BotOwner botOwner) : base(botOwner)
        {
            _nextMoveAt = Time.time + RandomRange(MinPauseSeconds, MaxPauseSeconds);
        }

        public override void Stop()
        {
            if (BotOwner != null) BotOwner.Sprint(false);
            _hasTarget = false;
        }

        public override void Update(CustomLayer.ActionData data)
        {
            if (BotOwner == null || BotOwner.Mover == null) return;

            CivilianDoorHelper.CheckAndOpenNearbyDoor(BotOwner);
            CivilianMeleeEnforcer.Tick(BotOwner);

            var botPos = BotOwner.Position;

            if (_hasTarget && Vector3.Distance(botPos, _target) < ArrivalDistance)
            {
                _hasTarget = false;
                _nextMoveAt = Time.time + RandomRange(MinPauseSeconds, MaxPauseSeconds);
            }

            if (_hasTarget || Time.time < _nextMoveAt) return;

            var angle = (float)_rng.NextDouble() * 360f;
            var dist = RandomRange(WanderMinDistance, WanderMaxDistance);
            var offset = Quaternion.Euler(0f, angle, 0f) * Vector3.forward * dist;
            var candidate = botPos + offset;

            var state = CivilianFleeState.Get(BotOwner);
            if (state.HasHome)
            {
                var fromHome = candidate - state.HomePosition;
                if (fromHome.magnitude > CivilianConstants.HomeZoneRadius)
                    candidate = state.HomePosition + fromHome.normalized * CivilianConstants.HomeZoneRadius;
            }

            var status = BotOwner.Mover.GoToPoint(candidate, false, 1f, false, true, false, false);
            if (status != NavMeshPathStatus.PathInvalid)
            {
                _target = candidate;
                _hasTarget = true;
            }
            else
            {
                _nextMoveAt = Time.time + 2f;
            }
        }

        private float RandomRange(float min, float max) =>
            min + (float)_rng.NextDouble() * (max - min);
    }
}
