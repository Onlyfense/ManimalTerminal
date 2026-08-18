using System;
using Audio.AmbientSubsystem;
using Audio.AmbientSubsystem.Data;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // RAIN AMBIENT SOUND (user report, twice): the PrecipitationAmbientBlenders in
    // our rebuilt ambient layer sit with NULL AudioSources/mixer, and their Init()
    // pulls the season sound data from AmbientAudioSystem.AmbientSoundData — also
    // null in the rebuild. this pass fills all of it: donor SeasonAmbientSoundDataSO
    // from loaded resources, two AudioSources per blender, an ambience mixer group,
    // then re-runs Init. every step logs so a miss names itself.
    internal static class TerminalRainAudio
    {
        private static bool _staged;

        internal static void ResetForRaid() => _staged = false;

        internal static void TryStage()
        {
            if (_staged || !TerminalGate.On) return;
            var sys = MonoBehaviourSingleton<AmbientAudioSystem>.Instantiated
                ? MonoBehaviourSingleton<AmbientAudioSystem>.Instance : null;
            if (sys == null) return; // ambient layer not staged yet
            var blenders = UnityEngine.Object.FindObjectsOfType<PrecipitationAmbientBlender>(true);
            if (blenders.Length == 0) return;
            _staged = true;

            try
            {
                // season sound data: the system's own, else any loaded donor SO
                var dataProp = AccessTools.Property(typeof(AmbientAudioSystem), "AmbientSoundData")
                    ?? null;
                object soData = dataProp?.GetValue(sys);
                if (soData == null)
                {
                    var sos = Resources.FindObjectsOfTypeAll<SeasonAmbientSoundDataSO>();
                    if (sos.Length > 0)
                    {
                        soData = sos[0];
                        if (dataProp != null && dataProp.CanWrite) dataProp.SetValue(sys, soData);
                        else AccessTools.Field(typeof(AmbientAudioSystem), "AmbientSoundData")?.SetValue(sys, soData);
                    }
                }
                Plugin.Log.LogInfo($"[RainAudio] season sound data: {(soData != null ? soData.ToString() : "NONE LOADED — rain stays silent, needs the SO shipped")}");

                UnityEngine.Audio.AudioMixerGroup mixer = null;
                foreach (var g in Resources.FindObjectsOfTypeAll<UnityEngine.Audio.AudioMixerGroup>())
                    if (g && g.name.IndexOf("ambien", StringComparison.OrdinalIgnoreCase) >= 0) { mixer = g; break; }

                int fixedN = 0;
                foreach (var b in blenders)
                {
                    var fSrc = AccessTools.Field(typeof(PrecipitationAmbientBlender), "_precipitationSource");
                    var fMix = AccessTools.Field(typeof(PrecipitationAmbientBlender), "_precipitationMixSource");
                    var fOut = AccessTools.Field(typeof(PrecipitationAmbientBlender), "_outputMixerGroup");
                    if (fSrc.GetValue(b) == null)
                    {
                        var s1 = b.gameObject.AddComponent<AudioSource>();
                        s1.playOnAwake = false; s1.loop = true; s1.spatialBlend = 0f;
                        fSrc.SetValue(b, s1);
                    }
                    if (fMix.GetValue(b) == null)
                    {
                        var s2 = b.gameObject.AddComponent<AudioSource>();
                        s2.playOnAwake = false; s2.loop = true; s2.spatialBlend = 0f;
                        fMix.SetValue(b, s2);
                    }
                    if (fOut.GetValue(b) == null && mixer != null) fOut.SetValue(b, mixer);
                    try { b.Init(); fixedN++; }
                    catch (Exception ie) { Plugin.Log.LogWarning($"[RainAudio] blender '{b.gameObject.name}' Init threw: {ie.Message}"); }
                }
                Plugin.Log.LogInfo($"[RainAudio] {fixedN}/{blenders.Length} precipitation blender(s) wired "
                    + $"(mixer: {(mixer ? mixer.name : "none")})");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[RainAudio] staging failed: {e}"); }
        }
    }
}
