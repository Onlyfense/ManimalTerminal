using BepInEx.Logging;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace Manimal.Terminal.Civilian.Behavior
{
    // per-tick flee: pick a cover point on the far side of the threat, sprint to
    // it, then crouch and cower. destination only re-rolls on arrival or repath —
    // per-tick GoToPoint thrashed the BotMover into spazzing-in-place.
    internal sealed class CivilianFleeLogic : CustomLogic
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Terminal.Civ");

        private const float ArrivalDistance = 2.5f;
        // fan-out either side of straight-away; wider = less "they all pick the same wall"
        private const float FleeConeDegrees = 75f;

        private Vector3 _destination;
        private bool _hasDestination;
        private bool _commandIssued;
        private readonly System.Random _rng = new System.Random();

        public CivilianFleeLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Start()
        {
            _hasDestination = false;
            _commandIssued = false;
            if (BotOwner != null)
            {
                BotOwner.Sprint(true);
                BotOwner.Mover?.SetTargetMoveSpeed(1f);
                SetCrouch(false);
            }
        }

        public override void Stop()
        {
            if (BotOwner != null)
            {
                BotOwner.Sprint(false);
                SetCrouch(false);
            }
            var state = CivilianFleeState.Get(BotOwner);
            state.InCover = false;
            state.CoweringUntil = 0f;
            _hasDestination = false;
            _commandIssued = false;
            CivilianUnstickHelper.Reset(BotOwner);
        }

        public override void Update(CustomLayer.ActionData data)
        {
            if (BotOwner == null || BotOwner.Mover == null) return;

            CivilianDoorHelper.CheckAndOpenNearbyDoor(BotOwner);
            CivilianMeleeEnforcer.Tick(BotOwner);

            var state = CivilianFleeState.Get(BotOwner);
            var botPos = BotOwner.Position;

            // cowering — stay put, stay crouched
            if (state.InCover && state.CoweringUntil > Time.time)
            {
                return;
            }

            var threat = CivilianThreatScanner.FindNearestThreat(BotOwner, CivilianConstants.ThreatDetectionRadius);

            if (threat.HasThreat)
            {
                var threatPos = threat.ThreatPosition;

                // new threat breaks an existing cower
                if (state.InCover)
                {
                    state.InCover = false;
                    state.CoweringUntil = 0f;
                    _hasDestination = false;
                    SetCrouch(false);
                }

                BotOwner.Sprint(true);

                // no mid-flight redirects — commit to the chosen spot
                if (!_hasDestination || ArrivedAt(_destination, botPos))
                {
                    PickRandomFleeDestination(threatPos, botPos);
                }
                IssueMoveIfNeeded();
                CheckStuck();
                return;
            }

            // no threat: on arrival, enter the cower phase
            if (_hasDestination && ArrivedAt(_destination, botPos))
            {
                if (!state.InCover)
                {
                    BotOwner.Sprint(false);
                    SetCrouch(true);
                    state.InCover = true;
                    state.CoweringUntil = Time.time + CivilianConstants.CowerDurationSeconds;
                }
            }
            else if (_hasDestination)
            {
                // threat dropped mid-run — keep walking to the committed spot
                BotOwner.Sprint(false);
                IssueMoveIfNeeded();
                CheckStuck();
            }
        }

        private void CheckStuck()
        {
            if (!_hasDestination) return;
            var status = CivilianUnstickHelper.Check(BotOwner, _destination);
            if (status == CivilianUnstickHelper.Status.NeedsRepath)
            {
                _hasDestination = false;
                _commandIssued = false;
            }
        }

        private void IssueMoveIfNeeded()
        {
            if (!_hasDestination || _commandIssued) return;
            // mustHaveWay=true so BotDoorOpener registers door waypoints along the
            // route — without it bots phase through closed doors
            var status = BotOwner.Mover.GoToPoint(_destination, false, 1f, false, true, false, false);
            if (status == NavMeshPathStatus.PathInvalid)
            {
                _hasDestination = false;
                return;
            }
            _commandIssued = true;
        }

        private void PickRandomFleeDestination(Vector3 threatPos, Vector3 botPos)
        {
            var away = (botPos - threatPos);
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f)
            {
                away = new Vector3((float)_rng.NextDouble() - 0.5f, 0f, (float)_rng.NextDouble() - 0.5f);
            }
            var awayNorm = away.normalized;

            var angle = ((float)_rng.NextDouble() * 2f - 1f) * FleeConeDegrees;
            var rotated = Quaternion.AngleAxis(angle, Vector3.up) * awayNorm;

            var distance = 12f + (float)_rng.NextDouble() * 10f; // 12-22m
            var target = botPos + rotated * distance;

            var cover = BotOwner.Covers?.FindClosestPoint(target, 0f, threatPos);
            var dest = cover != null ? cover.Position : target;

            // home tether: pull the flee back inside the zone radius
            var state = CivilianFleeState.Get(BotOwner);
            if (state.HasHome)
            {
                var home = state.HomePosition;
                var offset = dest - home;
                if (offset.magnitude > CivilianConstants.HomeZoneRadius)
                {
                    dest = home + offset.normalized * CivilianConstants.HomeZoneRadius;
                }
            }

            _destination = dest;
            _hasDestination = true;
            _commandIssued = false;
        }

        private static bool ArrivedAt(Vector3 dest, Vector3 botPos)
        {
            var d = dest - botPos;
            d.y = 0f;
            return d.sqrMagnitude < (ArrivalDistance * ArrivalDistance);
        }

        private void SetCrouch(bool crouch)
        {
            var ctx = BotOwner?.GetPlayer?.MovementContext;
            ctx?.SetPoseLevel(crouch ? 0f : 1f, false);
        }
    }
}
