using Comfort.Common;
using EFT;
using UnityEngine;

namespace Manimal.Terminal
{
    // spike + ramp forensics (2026-08-19: the chop is a monotonic RAMP — avg frame
    // cost climbs 16->38ms across a raid regardless of artillery; something
    // accumulates). three instruments, all near-free between events:
    //  - spike lines: >50ms frames with position/bots/water/artillery context and
    //    OURS attribution (Plugin.Update tick cost measured by stopwatch), ported
    //    from icebreaker's stutter probe — UNTRACKED means "not this component"
    //  - last-8-frame ring + gc deltas on each spike: one giant frame vs creep
    //  - 60s census: object counts that only ever grow reveal the leak (renderers,
    //    audio sources, particle systems, gameobject total via scene roots)
    internal static class TerminalPerfWatch
    {
        private static float _lastLog;
        private static int _spikes;
        private static System.Collections.Generic.List<Renderer> _water;

        // OURS attribution: Plugin.Update wraps its tick block with Begin/End
        private static readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();
        private static double _oursMs;
        internal static void OursBegin() => _sw.Restart();
        internal static void OursEnd() => _oursMs = _sw.Elapsed.TotalMilliseconds;

        // last-8 frame ring
        private static readonly float[] _ring = new float[8];
        private static int _ringIdx;
        private static int _gc0Prev;
        private static float _avg = 1f / 60f;

        // census
        private static float _nextCensusAt;
        private static int _censusNum;

        internal static void ResetForRaid()
        {
            _spikes = 0;
            _lastLog = 0f;
            _water = null;
            _nextCensusAt = 0f;
            _censusNum = 0;
        }

        internal static void WatchWater(System.Collections.Generic.List<Renderer> planes) => _water = planes;

        internal static void Tick()
        {
            if (!TerminalGate.On) return;
            float dt = Time.unscaledDeltaTime;
            int gc0 = System.GC.CollectionCount(0);

            var gw = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            var p = gw != null ? gw.MainPlayer : null;

            // 60s census — the ramp fingerprint. counts that only grow = the leak.
            if (p != null && Time.realtimeSinceStartup >= _nextCensusAt)
            {
                _nextCensusAt = Time.realtimeSinceStartup + 60f;
                _censusNum++;
                try
                {
                    int renderers = Object.FindObjectsOfType<Renderer>().Length;
                    int audio = Object.FindObjectsOfType<AudioSource>().Length;
                    int particles = Object.FindObjectsOfType<ParticleSystem>().Length;
                    int alive = 0;
                    try { alive = gw.AllAlivePlayersList?.Count ?? 0; } catch { }
                    Plugin.Log.LogWarning($"[Perf][census #{_censusNum}] avgFrame={_avg * 1000f:F1}ms renderers={renderers}"
                        + $" audioSources={audio} particles={particles} alive={alive}");
                }
                catch { }
            }

            if (dt >= 0.05f)
            {
                _spikes++;
                if (Time.realtimeSinceStartup - _lastLog >= 2f)
                {
                    _lastLog = Time.realtimeSinceStartup;
                    int alive = -1;
                    try { alive = gw != null && gw.AllAlivePlayersList != null ? gw.AllAlivePlayersList.Count : -1; } catch { }
                    int waterVis = 0;
                    if (_water != null)
                        foreach (var r in _water)
                            if (r && r.isVisible) waterVis++;
                    int artyZones = -1;
                    try { artyZones = TerminalArtillery.ActiveZonesDict()?.Count ?? -1; } catch { }
                    var ring = new System.Text.StringBuilder();
                    for (int i = 1; i <= _ring.Length; i++)
                        ring.Append((_ring[(_ringIdx + i) % _ring.Length] * 1000f).ToString("F0")).Append(' ');
                    Plugin.Log.LogWarning($"[Perf] {dt * 1000f:F0}ms frame (spike #{_spikes})"
                        + $" at {(p != null ? p.Position.ToString() : "?")}, {alive} alive"
                        + $", water visible: {waterVis}, artyZones={artyZones} pump={(TerminalArtillery.Pumping ? "on" : "off")}"
                        + $" | OURS={_oursMs:F1}ms gc0Δ={gc0 - _gc0Prev} avg={_avg * 1000f:F1}ms prev8: {ring.ToString().TrimEnd()}");
                }
            }

            _ring[_ringIdx] = dt;
            _ringIdx = (_ringIdx + 1) % _ring.Length;
            _gc0Prev = gc0;
            _avg = Mathf.Lerp(_avg, dt, 0.05f);
        }
    }
}
