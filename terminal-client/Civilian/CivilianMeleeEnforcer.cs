using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace Manimal.Terminal.Civilian
{
    // belt-and-braces for the knife redirect: the first raid had civilians with
    // pistols DRAWN despite the TryChangeToSlot prefix — some arming path dodges
    // the selector virtual. whenever a civilian's hands hold a firearm, request
    // ChangeToMelee (the selector's own public path to the Scabbard, where the
    // InfectionArms fake melee sits). throttled; skipped while a change is
    // already in flight.
    internal static class CivilianMeleeEnforcer
    {
        private static readonly Dictionary<string, float> NextCheckAt = new Dictionary<string, float>();

        public static void Tick(BotOwner bot)
        {
            try
            {
                var player = bot?.GetPlayer;
                if (player == null) return;
                var id = bot.Profile?.Id;
                if (id == null) return;

                if (NextCheckAt.TryGetValue(id, out var next) && Time.time < next) return;
                NextCheckAt[id] = Time.time + 1.5f;

                if (!(player.HandsController is Player.FirearmController)) return;

                var selector = bot.WeaponManager?.Selector;
                if (selector == null || selector.IsChanging) return;
                selector.ChangeToMelee();
                Plugin.Log.LogInfo($"[Civ] {bot.Profile?.Nickname}: firearm in hands — forcing melee");
            }
            catch { }
        }

        public static void ClearAll() => NextCheckAt.Clear();
    }
}
