using System;
using HarmonyLib;

namespace Manimal.Terminal
{
    // THE ENDGAME CHOP (perf hunt 2026-08-18): TimerPanel.SetTimerText ->
    // SetMonospaceText -> TMP GenerateTextMesh NREs from raid start, ~1/s, each
    // throw logging a full stack twice (Unity + UnityExplorer) — the oscillating
    // frame-spike windows all raid. armor swallows the throw at the sink; the
    // timer text just skips a tick when the panel's TMP internals are null.
    internal static class TerminalTimerArmor
    {
        private static bool _reported;

        [HarmonyPatch(typeof(EFT.UI.BattleTimer.TimerPanel), "SetTimerText")]
        internal static class Patch_TimerTextArmor
        {
            [HarmonyFinalizer]
            private static Exception Finalizer(Exception __exception)
            {
                if (__exception != null && !_reported)
                {
                    _reported = true;
                    Plugin.Log.LogWarning($"[TimerArmor] SetTimerText threw (suppressed from here on): {__exception.Message}");
                }
                return null;
            }
        }
    }
}
