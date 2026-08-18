using Comfort.Common;
using EFT;
using UnityEngine;

namespace Manimal.Terminal
{
    // spike forensics (user report: "massive lag spikes towards the end of the map"):
    // logs frame time + player position + alive-bot count on every >50ms frame, so a
    // raid log shows whether spikes track LOCATION (render/culling) or RAID PHASE
    // (bot pileup). read-only — costs nothing between spikes.
    internal static class TerminalPerfWatch
    {
        private static float _lastLog;
        private static int _spikes;
        private static System.Collections.Generic.List<Renderer> _water;

        internal static void ResetForRaid()
        {
            _spikes = 0;
            _lastLog = 0f;
            _water = null;
        }

        // the water planes are the prime suspect for the port-area fps tanking —
        // correlate spikes with their actual on-screen visibility
        internal static void WatchWater(System.Collections.Generic.List<Renderer> planes) => _water = planes;

        internal static void Tick()
        {
            if (!TerminalGate.On) return;
            float dt = Time.unscaledDeltaTime;
            if (dt < 0.05f) return;
            _spikes++;
            if (Time.realtimeSinceStartup - _lastLog < 2f) return; // one line per burst
            _lastLog = Time.realtimeSinceStartup;
            var gw = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            var p = gw != null ? gw.MainPlayer : null;
            int alive = -1;
            try { alive = gw != null && gw.AllAlivePlayersList != null ? gw.AllAlivePlayersList.Count : -1; } catch { }
            int waterVis = 0;
            if (_water != null)
                foreach (var r in _water)
                    if (r && r.isVisible) waterVis++;
            Plugin.Log.LogWarning($"[Perf] {dt * 1000f:F0}ms frame (spike #{_spikes})"
                + $" at {(p != null ? p.Position.ToString() : "?")}, {alive} alive"
                + $", water visible: {waterVis}");
        }
    }
}
