using System;
using UnityEngine;

namespace Manimal.Terminal
{
    // shared handle on terminal_fx.bundle (plugin folder, built by Terminal/Build FX
    // Bundle in the SDK) — a second LoadFromFile on an already-loaded bundle returns
    // null, so every consumer (stencil shader, gate audio) goes through here
    internal static class TerminalFxBundle
    {
        private static AssetBundle _bundle;
        private static bool _tried;

        internal static AssetBundle Get()
        {
            if (_tried) return _bundle;
            _tried = true;
            try
            {
                var path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                    "terminal_fx.bundle");
                if (!System.IO.File.Exists(path))
                {
                    Plugin.Log.LogWarning("[FxBundle] terminal_fx.bundle missing — run Terminal/Build FX Bundle in the SDK");
                    return null;
                }
                _bundle = AssetBundle.LoadFromFile(path);
                if (_bundle == null)
                    Plugin.Log.LogWarning("[FxBundle] terminal_fx.bundle failed to open");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[FxBundle] load failed: {e.Message}"); }
            return _bundle;
        }

        // clip lookup: fx bundle first, then anything already loaded (scene bundle,
        // game banks). cached for the session
        private static readonly System.Collections.Generic.Dictionary<string, AudioClip> _clips
            = new System.Collections.Generic.Dictionary<string, AudioClip>();

        internal static AudioClip FindClip(string name)
        {
            if (_clips.TryGetValue(name, out var cached) && cached) return cached;
            var bundle = Get();
            var clip = bundle != null ? bundle.LoadAsset<AudioClip>(name) : null;
            if (!clip)
            {
                foreach (var c in Resources.FindObjectsOfTypeAll<AudioClip>())
                    if (c != null && c.name == name) { clip = c; break; }
            }
            if (clip) _clips[name] = clip;
            else Plugin.Log.LogWarning($"[FxBundle] clip '{name}' not found — rebuild Terminal/Build FX Bundle in the SDK");
            return clip;
        }
    }
}
