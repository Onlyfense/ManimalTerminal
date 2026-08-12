using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // hold ALL bot waves until the attack cutscene has played (user call 2026-08-10:
    // distant firefights during the cutscene — the port shouldnt be at war before the
    // attack happens). every spawn path funnels through BotsController.ActivateBotsByWave
    // (LocalGame hands both scenario spawn actions to it), so gating the two overloads
    // holds waves, bosses and triggered events alike; held waves queue and flush the
    // moment the cutscene ends. escape hatches: gate never holds when the cutscene is
    // unavailable (missing bundle piece) and never past a hard 8min ceiling.
    internal static class TerminalSpawnGate
    {
        private static float _raidStart = -1f;
        private static bool _holdLogged;
        private static readonly List<(BotsController c, BotWaveDataClass w)> _waves = new();
        private static readonly List<(BotsController c, BossLocationSpawn w)> _bosses = new();

        internal static bool Open
        {
            get
            {
                if (!Plugin.HoldBotsForCutscene.Value) return true;
                if (TerminalAttackCutscene.FinishedAt > 0f) return true;
                if (!TerminalAttackCutscene.Available) return true;
                if (_raidStart > 0f && Time.realtimeSinceStartup - _raidStart > 480f) return true;
                return false;
            }
        }

        private static void LogHold(string what)
        {
            if (_holdLogged) return;
            _holdLogged = true;
            Plugin.Log.LogInfo($"[SpawnGate] holding bot spawns until the attack cutscene ends (first held: {what})");
        }

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_ArmGate
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!TerminalGate.On) return;
                _raidStart = Time.realtimeSinceStartup;
                _holdLogged = false;
                _waves.Clear();
                _bosses.Clear();
                if (Plugin.HoldBotsForCutscene.Value)
                    new GameObject("Terminal_SpawnGate").AddComponent<FlushHost>();
            }
        }

        [HarmonyPatch(typeof(BotsController), nameof(BotsController.ActivateBotsByWave), typeof(BotWaveDataClass))]
        internal static class Patch_GateWaves
        {
            [HarmonyPrefix]
            private static bool Prefix(BotsController __instance, BotWaveDataClass wave, ref Task __result)
            {
                if (!TerminalGate.On || Open) return true;
                _waves.Add((__instance, wave));
                LogHold("assault wave");
                __result = Task.CompletedTask;
                return false;
            }
        }

        [HarmonyPatch(typeof(BotsController), nameof(BotsController.ActivateBotsByWave), typeof(BossLocationSpawn))]
        internal static class Patch_GateBosses
        {
            [HarmonyPrefix]
            private static bool Prefix(BotsController __instance, BossLocationSpawn wave)
            {
                if (!TerminalGate.On || Open) return true;
                _bosses.Add((__instance, wave));
                LogHold("boss/event wave");
                return false;
            }
        }

        // the non-wave scenario spawns through its own Update loop, not the wave
        // choke — freeze it whole while the gate is closed
        [HarmonyPatch(typeof(NonWavesSpawnScenario), nameof(NonWavesSpawnScenario.Update))]
        internal static class Patch_GateNonWaves
        {
            [HarmonyPrefix]
            private static bool Prefix() => !TerminalGate.On || Open;
        }

        internal class FlushHost : MonoBehaviour
        {
            private float _next;

            private void Update()
            {
                if (Time.realtimeSinceStartup < _next) return;
                _next = Time.realtimeSinceStartup + 1f;
                if (!Open) return;
                if (_waves.Count + _bosses.Count > 0)
                {
                    Plugin.Log.LogInfo($"[SpawnGate] cutscene done — releasing {_waves.Count} wave(s) + {_bosses.Count} boss wave(s)");
                    foreach (var (c, w) in _waves)
                        try { _ = c.ActivateBotsByWave(w); } catch (Exception e) { Plugin.Log.LogWarning($"[SpawnGate] wave release failed: {e.Message}"); }
                    foreach (var (c, w) in _bosses)
                        try { c.ActivateBotsByWave(w); } catch (Exception e) { Plugin.Log.LogWarning($"[SpawnGate] boss release failed: {e.Message}"); }
                    _waves.Clear();
                    _bosses.Clear();
                }
                Destroy(gameObject); // gate is open — everything new passes straight through
            }
        }
    }
}
