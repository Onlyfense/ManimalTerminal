using EFT;

namespace Manimal.Terminal.Civilian
{
    // ported from MitsuruMod 2026-08-18, recruitment/follow/loot/exfil stripped —
    // terminal civilians only wander, hide and flee (the retail 1.0 brain is just
    // AvoidDangerLayer + HideInCoversLayer + a weapon selector that never draws).
    // 776701 must keep a pistol in Holster: EFT never transitions melee-only bots
    // out of PreActive (the unarmed 776700 variant died to that).
    internal static class CivilianConstants
    {
        public const int WildSpawnTypeValue = 776701;

        // radius within which an entity with a firearm DRAWN counts as a threat
        public const float ThreatDetectionRadius = 25f;

        // after the threat disappears, keep running to the chosen spot this long
        public const float ThreatMemorySeconds = 8f;

        // civilians stay tethered to their spawn point — keeps the ship/pump/gate
        // groups in their authored areas
        public const float HomeZoneRadius = 30f;

        // crouched-in-cover freeze after arriving with no threat in sight;
        // intentionally blind to new threats while cowering
        public const float CowerDurationSeconds = 5f;

        // gunshots panic through walls within this radius (no LoS check)
        public const float GunshotHearingRadius = 45f;

        // how long a heard shot stays valid as a flee trigger
        public const float GunshotMemorySeconds = 6f;

        public static bool IsCivilian(WildSpawnType type) => (int)type == WildSpawnTypeValue;
    }
}
