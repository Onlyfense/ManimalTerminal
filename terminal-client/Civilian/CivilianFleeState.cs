using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace Manimal.Terminal.Civilian
{
    // shared state between CivilianFleeLayer and CivilianFleeLogic — BigBrain
    // instantiates layer and logic as separate unlinked objects, so per-bot flee
    // state parks in a static dict keyed by profile id. ClearAll per raid or the
    // dict leaks across raids (MitsuruMod never called it — ported fix).
    internal static class CivilianFleeState
    {
        public sealed class State
        {
            public float LastThreatSeenAt = -999f;
            // Time.time when post-hide cowering ends; while > now the layer stays
            // active and does NOT scan — frozen in hiding
            public float CoweringUntil;
            public bool InCover;

            // spawn-point tether for wander/flee clamping
            public Vector3 HomePosition;
            public bool HasHome;

            // stamped by GunshotHearingPatch — gunfire panics through walls
            public float LastGunshotAt = -999f;
            public Vector3 LastGunshotPos;
        }

        private static readonly Dictionary<string, State> States = new Dictionary<string, State>();

        public static State Get(BotOwner owner)
        {
            var id = owner?.Profile?.Id;
            if (string.IsNullOrEmpty(id)) return new State();
            if (!States.TryGetValue(id, out var s))
            {
                s = new State();
                States[id] = s;
            }
            return s;
        }

        public static void ClearAll() => States.Clear();

        // records the spawn point as home; idempotent
        public static void EnsureHomeInitialized(BotOwner owner)
        {
            if (owner == null) return;
            var s = Get(owner);
            if (s.HasHome) return;
            s.HomePosition = owner.Position;
            s.HasHome = true;
        }
    }
}
