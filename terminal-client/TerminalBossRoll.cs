using System.Collections.Generic;
using UnityEngine;

namespace Manimal.Terminal
{
    // THE BOSS ROLL (user-preferred implementation, 2026-08-18 — replaces the old
    // parse-time PortBossPool): retail terminal spawns exactly ONE of five bosses
    // per raid at the container ship berths (Glukhar / Killa / Reshala / Sanitar /
    // Tagilla). the db ships all five rows on the same zone/trigger with the
    // live-dump-verified escorts; at location capture we pick one and REMOVE the
    // rest — array surgery, not chance edits, because the rows carry
    // ForceSpawn=true which can ignore BossChance.
    internal static class TerminalBossRoll
    {
        private static readonly string[] Candidates =
            { "bossGluhar", "bossKilla", "bossBully", "bossSanitar", "bossTagilla" };

        internal static void Roll(LocationSettingsClass.Location location)
        {
            try
            {
                var rows = location?.BossLocationSpawn;
                if (rows == null || rows.Length == 0) return;
                var pool = new List<int>();
                for (int i = 0; i < rows.Length; i++)
                {
                    var n = rows[i]?.BossName;
                    foreach (var c in Candidates) if (n == c) { pool.Add(i); break; }
                }
                if (pool.Count <= 1) return; // nothing to roll
                int winner = pool[Random.Range(0, pool.Count)];

                var kept = new List<BossLocationSpawn>(rows.Length);
                for (int i = 0; i < rows.Length; i++)
                    if (i == winner || !pool.Contains(i)) kept.Add(rows[i]);
                location.BossLocationSpawn = kept.ToArray();
                Plugin.Log.LogInfo($"[BossRoll] {pool.Count} candidates -> '{rows[winner].BossName}' "
                    + $"(escort {rows[winner].BossEscortAmount}x {rows[winner].BossEscortType}) spawns this raid");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[BossRoll] failed: {e.Message}"); }
        }
    }
}
