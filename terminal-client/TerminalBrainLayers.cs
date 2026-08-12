using System;
using System.Collections.Generic;
using Comfort.Common;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace Manimal.Terminal
{
    // BIGBRAIN CREW LAYERS — icebreaker's IceCrewJobs port, trimmed to what terminal
    // needs today. vanilla patrol layers are passive wanderers: the TB-triggered
    // assault waves spawned and strolled their zone routes instead of storming the
    // tier line (2026-08-10 raid). retail's read is "the port pushes back" — so every
    // bot born off a tier event gets a HUNT job with a rush window: sprint at the
    // players until contact, then the normal combat AI owns the fight.
    //
    // priorities carry icebreaker's measured numbers: crew 68 owns the idle slot
    // (above vanilla idle <=65, below combat >=70, stands down on any live threat);
    // rush 110 clears AvoidDanger (ExUsec 100 / PMC 80) so a deploy order actually
    // executes, yielding only on a VISIBLE enemy. hold tier + speaker muting NOT
    // ported — terminal has no scripted ambush squads yet.
    internal static class TerminalCrewJobs
    {
        internal enum Job { None, Guard, Hunt }

        internal sealed class Rec
        {
            public Job Job;
            public Bounds Zone;
            public float RushUntil;
        }

        private static readonly Dictionary<string, Rec> ByProfile = new Dictionary<string, Rec>();

        // the last tier event: bots created inside the window get the push job. wave
        // spawn requests go through the server (profile gen takes a while), so the
        // window is generous — retail waves land inside a couple of minutes.
        private static float _eventAt = -1f;
        private const float EventWindow = 180f;

        private static bool _registered;

        internal static void Register()
        {
            if (_registered) return;
            _registered = true;
            // Assault = the scav brain the TB waves run; PMC-family = blackdiv/RUAF
            // (icebreaker's post-release diagnostic: faction mods run the LITERAL
            // 'PMC' brain; ExUsec included for any rogue-brained strays)
            var brains = new List<string> { "Assault", "CursedAssault", "PMC", "PmcBear", "PmcUsec", "ExUsec" };
            BrainManager.AddCustomLayer(typeof(TerminalCrewLayer), brains, 68);
            BrainManager.AddCustomLayer(typeof(TerminalRushLayer), brains, 110);
            Plugin.Log.LogInfo("[CrewLayer] bigbrain layers registered (Assault/PMC-family, crew 68 / rush 110)");
        }

        internal static void NoteEvent(string name)
        {
            _eventAt = Time.time;
            Plugin.Log.LogDebug($"[CrewLayer] tier event '{name}' noted — bots spawning in the next {EventWindow:0}s push the players");
            if (string.Equals(name, "TB8", StringComparison.OrdinalIgnoreCase))
                TerminalHangarCrew.Arm(); // count the squad shortly after the wave, not on first-guard luck
        }

        // the BD_hold marker volume (retail AIPlaceInfo, the one with NO event logic):
        // black division born inside it guards the hangar — they camp the keycard
        // suitcase instead of joining the push (user call 2026-08-10). resolved lazily;
        // the box is a runtime-rebuilt trigger so it exists by the time bots spawn.
        private static Bounds? _bdHold;
        private static bool _bdHoldLooked;

        // remembered so the hangar top-up can still name the role after its boss dies
        internal static WildSpawnType? LastBlackDivRole;

        private static Bounds? BdHoldBounds()
        {
            if (_bdHoldLooked) return _bdHold;
            _bdHoldLooked = true;
            try
            {
                var go = GameObject.Find("BD_hold");
                var col = go != null ? go.GetComponent<BoxCollider>() : null;
                if (col != null) _bdHold = col.bounds;
            }
            catch { }
            if (_bdHold == null) Plugin.Log.LogWarning("[CrewLayer] BD_hold volume not found — hangar guard duty off");
            return _bdHold;
        }

        // called from BotSpawner.OnBotCreated — event-window bots hunt with a rush
        internal static void OnBotCreated(BotOwner bot)
        {
            try
            {
                if (!TerminalGate.On || !Plugin.EventWavesPush.Value || bot?.ProfileId == null) return;
                if (bot.GetPlayer != null && bot.GetPlayer.IsYourPlayer) return;
                // blackdiv is decided LAZILY in For() — at OnBotCreated the bot isnt at
                // its spawn point yet (2026-08-10 raid: every hangar BD read as outside
                // BD_hold and took the hunt job), the first brain-tick query runs with a
                // settled position
                var r0 = bot.Profile?.Info?.Settings?.Role.ToString() ?? "";
                if (r0.IndexOf("blackdiv", StringComparison.OrdinalIgnoreCase) >= 0) return;
                if (_eventAt < 0f || Time.time - _eventAt > EventWindow) return;
                // RUAF stays off the push list — they're authored NEUTRAL to the player
                // (2026-08-10: the hunt job marched them into the player's face and the
                // encounter turned into a firefight). only the hostile wave roles storm:
                // scav assault + blackdiv. role names resolve because the faction
                // prepatchers inject real enum members.
                var role = bot.Profile?.Info?.Settings?.Role.ToString() ?? "";
                bool hostileWaveRole = role.IndexOf("assault", StringComparison.OrdinalIgnoreCase) >= 0
                                    && role.IndexOf("ruaf", StringComparison.OrdinalIgnoreCase) < 0
                                    && role.IndexOf("vsrf", StringComparison.OrdinalIgnoreCase) < 0;
                if (!hostileWaveRole)
                {
                    Plugin.Log.LogDebug($"[CrewLayer] {bot.name}: role '{role}' not a push role — left to its own brain");
                    return;
                }
                ByProfile[bot.ProfileId] = new Rec { Job = Job.Hunt, RushUntil = Time.time + 60f };
                Plugin.Log.LogDebug($"[CrewLayer] {bot.name}: HUNT (event wave, 60s rush) role='{role}' brain='{bot.Brain?.BaseBrain?.ShortName()}'");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[CrewLayer] assign failed: {e.Message}"); }
        }

        // blackdiv placement check runs here, on the layer's own queries — the bot is
        // placed and ticking by then. 2s grace (icebreaker's FirstSeen pattern) so a
        // mid-teleport first tick cant mis-file them.
        private static readonly Dictionary<string, float> FirstSeen = new Dictionary<string, float>();

        internal static Rec For(BotOwner bot)
        {
            var id = bot?.ProfileId;
            if (id == null) return null;
            if (ByProfile.TryGetValue(id, out var rec)) return rec;

            var role = bot.Profile?.Info?.Settings?.Role.ToString() ?? "";
            if (role.IndexOf("blackdiv", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (LastBlackDivRole == null && bot.Profile?.Info?.Settings != null)
                    LastBlackDivRole = bot.Profile.Info.Settings.Role;
                if (!FirstSeen.TryGetValue(id, out var seen)) { FirstSeen[id] = Time.time; return null; }
                if (Time.time - seen < 2f) return null;
                // EXPLICIT PER-ZONE BEHAVIOR TABLE (retail script per user, 2026-08-11):
                //   Zone1BD1HangarAmbush11 — the TB4 armory push: ~8 BD in staggered
                //     waves CHARGE the player's position, brawl the armory scavs/ruaf.
                //     always hunt, no event-window requirement.
                //   Zone1BD1HangarBD11 — the keycard squad: camp BD_hold, guard the
                //     valbergs. (zone name beats the position race; topper spawns carry
                //     the same zone.)
                //   other *Ambush* zones (GateAmbush13's 950s pair) — hold their post.
                //   everything else — event-window push as before.
                var zname = bot.BotsGroup?.BotZone?.name ?? "";
                if (zname.IndexOf("HangarAmbush", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ByProfile[id] = new Rec { Job = Job.Hunt, RushUntil = Time.time + 60f };
                    Plugin.Log.LogDebug($"[CrewLayer] {bot.name}: HUNT (armory push) role='{role}' zone='{zname}' pos={bot.Position}");
                    return ByProfile[id];
                }
                if (zname.IndexOf("HangarBD", StringComparison.OrdinalIgnoreCase) >= 0
                    && BdHoldBounds() is Bounds holdZ)
                {
                    ByProfile[id] = new Rec { Job = Job.Guard, Zone = holdZ };
                    Plugin.Log.LogDebug($"[CrewLayer] {bot.name}: GUARD BD_hold (hangar camp) role='{role}' zone='{zname}' pos={bot.Position}");
                    TerminalHangarCrew.Arm();
                    return ByProfile[id];
                }
                if (zname.IndexOf("Ambush", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ByProfile[id] = new Rec { Job = Job.Guard, Zone = new Bounds(bot.Position, new Vector3(24f, 8f, 24f)) };
                    Plugin.Log.LogDebug($"[CrewLayer] {bot.name}: GUARD ambush post role='{role}' zone='{zname}' pos={bot.Position}");
                    return ByProfile[id];
                }
                // zone-less fallback: position against the hold box with the spawn-mouth
                // reach margin (topper/odd spawn paths)
                if (BdHoldBounds() is Bounds hold)
                {
                    var reach = hold;
                    reach.Expand(new Vector3(30f, 12f, 30f));
                    if (reach.Contains(bot.Position))
                    {
                        ByProfile[id] = new Rec { Job = Job.Guard, Zone = hold };
                        Plugin.Log.LogDebug($"[CrewLayer] {bot.name}: GUARD BD_hold (position fallback) role='{role}' pos={bot.Position}");
                        TerminalHangarCrew.Arm();
                        return ByProfile[id];
                    }
                }
                if (_eventAt > 0f && Time.time - _eventAt <= EventWindow)
                {
                    ByProfile[id] = new Rec { Job = Job.Hunt, RushUntil = Time.time + 60f };
                    Plugin.Log.LogDebug($"[CrewLayer] {bot.name}: HUNT (event wave, lazy) role='{role}' pos={bot.Position}");
                }
                else ByProfile[id] = null; // cached negative
                return ByProfile[id];
            }
            ByProfile[id] = null;
            return null;
        }

        internal static Bounds? BdHoldBoundsPublic() => BdHoldBounds();

        internal static void Reset()
        {
            ByProfile.Clear(); FirstSeen.Clear(); _eventAt = -1f; _bdHold = null; _bdHoldLooked = false;
            LastBlackDivRole = null;
            TerminalHangarCrew.ResetForRaid();
        }
    }

    internal class TerminalCrewLayer : CustomLayer
    {
        public TerminalCrewLayer(BotOwner botOwner, int priority) : base(botOwner, priority) { }

        public override string GetName() => "TerminalCrew";

        public override bool IsActive()
        {
            if (!TerminalGate.On) return false;
            var rec = TerminalCrewJobs.For(BotOwner);
            if (rec == null || rec.Job == TerminalCrewJobs.Job.None) return false;
            var p = BotOwner.GetPlayer;
            if (p == null || p.HealthController == null || !p.HealthController.IsAlive) return false;
            // any live threat = stand down instantly; combat layers own the bot
            try
            {
                if (BotOwner.Memory != null && (BotOwner.Memory.GoalEnemy != null || BotOwner.Memory.IsUnderFire))
                    return false;
            }
            catch { return false; }
            return true;
        }

        public override Action GetNextAction()
        {
            switch (TerminalCrewJobs.For(BotOwner)?.Job)
            {
                case TerminalCrewJobs.Job.Hunt: return new Action(typeof(TerminalHuntLogic), "hunt the players");
                default: return new Action(typeof(TerminalGuardLogic), "guard the zone");
            }
        }

        public override bool IsCurrentActionEnding()
        {
            Type want;
            switch (TerminalCrewJobs.For(BotOwner)?.Job)
            {
                case TerminalCrewJobs.Job.Hunt: want = typeof(TerminalHuntLogic); break;
                default: want = typeof(TerminalGuardLogic); break;
            }
            return CurrentAction == null || CurrentAction.Type != want;
        }
    }

    // deployment override: while the rush window is open the bot MOVES — no
    // enemy-memory gate (noise must not hold the wave in its spawn zone), yields
    // only on a visible enemy so combat takes over at contact
    internal class TerminalRushLayer : CustomLayer
    {
        public TerminalRushLayer(BotOwner botOwner, int priority) : base(botOwner, priority) { }

        public override string GetName() => "TerminalCrewRush";

        public override bool IsActive()
        {
            if (!TerminalGate.On) return false;
            var rec = TerminalCrewJobs.For(BotOwner);
            if (rec == null || rec.Job == TerminalCrewJobs.Job.None || Time.time >= rec.RushUntil) return false;
            var p = BotOwner.GetPlayer;
            if (p == null || p.HealthController == null || !p.HealthController.IsAlive) return false;
            try
            {
                var ge = BotOwner.Memory?.GoalEnemy;
                if (ge != null && ge.IsVisible) return false; // sight ends the charge
            }
            catch { }
            return true;
        }

        public override Action GetNextAction()
            => new Action(typeof(TerminalHuntLogic), "rush the players");

        public override bool IsCurrentActionEnding()
            => CurrentAction == null || CurrentAction.Type != typeof(TerminalHuntLogic);
    }

    // roam the assigned box at a watchful walk (unused until something assigns Guard,
    // but the layer contract needs it)
    internal class TerminalGuardLogic : CustomLogic
    {
        private float _next;

        public TerminalGuardLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Update(CustomLayer.ActionData data)
        {
            if (Time.time < _next) return;
            _next = Time.time + UnityEngine.Random.Range(6f, 12f);
            var rec = TerminalCrewJobs.For(BotOwner);
            if (rec == null) return;
            var z = rec.Zone;
            var want = new Vector3(
                UnityEngine.Random.Range(z.min.x, z.max.x),
                z.center.y,
                UnityEngine.Random.Range(z.min.z, z.max.z));
            if (!NavMesh.SamplePosition(want, out var hit, 4f, NavMesh.AllAreas)) return;
            BotOwner.Mover?.SetTargetMoveSpeed(0.5f);
            BotOwner.Mover?.SetPose(1f);
            BotOwner.Mover?.GoToPoint(hit.position, true, 0.6f);
        }

        public override void Stop()
        {
            base.Stop();
            _next = 0f;
        }
    }

    // push toward the player: repath every few seconds, sprint when far, slow to a
    // hunting walk close-in so vision/hearing acquire before contact. 4m reach —
    // the goal is contact, not a hug. solo-correct (main player only), fika bridge
    // stays deferred with the rest of terminal's fika gating.
    internal class TerminalHuntLogic : CustomLogic
    {
        private float _next;

        public TerminalHuntLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Update(CustomLayer.ActionData data)
        {
            if (Time.time < _next) return;
            _next = Time.time + 3f;

            var target = Singleton<GameWorld>.Instance?.MainPlayer;
            if (target == null || target.HealthController == null || !target.HealthController.IsAlive) return;

            float sq = (target.Position - BotOwner.Position).sqrMagnitude;
            bool far = sq > 20f * 20f;
            BotOwner.Mover?.SetPose(1f);
            BotOwner.Mover?.SetTargetMoveSpeed(far ? 1f : 0.7f);
            try { BotOwner.Sprint(far, true); } catch { }
            BotOwner.Mover?.GoToPoint(target.Position, true, 4f);
        }

        public override void Stop()
        {
            base.Stop();
            try { BotOwner.Sprint(false, true); } catch { }
            _next = 0f;
        }
    }
}
