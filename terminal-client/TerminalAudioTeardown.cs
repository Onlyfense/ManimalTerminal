using System;
using HarmonyLib;

namespace Manimal.Terminal
{
    // SESSION-END ARMOR (2026-08-18: full playthrough ended in a stuck black
    // screen — the survived screen never came because TarkovApplication's session
    // teardown died on AmbientAudioSystem.Dispose -> EnvironmentSoundBlendSystem.
    // Dispose NRE, a null internal in our rebuilt ambient layer). the raid end
    // must never hinge on audio teardown: swallow-and-log finalizers on both.
    internal static class TerminalAudioTeardown
    {
        [HarmonyPatch(typeof(Audio.AmbientSubsystem.AmbientAudioSystem), "Dispose")]
        internal static class Patch_AmbientDisposeArmor
        {
            [HarmonyFinalizer]
            private static Exception Finalizer(Exception __exception)
            {
                if (__exception != null && TerminalGate.On)
                {
                    Plugin.Log.LogWarning($"[AudioTeardown] AmbientAudioSystem.Dispose threw (swallowed, session end continues): {__exception.Message}");
                    return null;
                }
                return __exception;
            }
        }

        [HarmonyPatch(typeof(Audio.AmbientSubsystem.EnvironmentSoundBlendSystem), "Dispose")]
        internal static class Patch_BlendDisposeArmor
        {
            [HarmonyFinalizer]
            private static Exception Finalizer(Exception __exception)
            {
                if (__exception != null && TerminalGate.On)
                {
                    Plugin.Log.LogWarning($"[AudioTeardown] EnvironmentSoundBlendSystem.Dispose threw (swallowed): {__exception.Message}");
                    return null;
                }
                return __exception;
            }
        }
    }
}
