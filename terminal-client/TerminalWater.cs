using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace Manimal.Terminal
{
    // WATER SURFACES (raid report 2026-08-18: pump reservoir + sea planes invisible
    // or garbage) — the rip bound the water materials to WRONG shaders entirely
    // (City_Water_Dirt -> Cloth/ClothShader), which the name-keyed rebind pass
    // can't detect: the wrong shader is a valid one. 4.0 SHIPS the SSR water
    // system (WaterSSR.WaterForSSRv3 + WaterRendererv3, 'Draw Water v3') — the
    // proper restore authors a WaterForSSRv3 over the plane MeshFilters and lets
    // the native renderer draw them with its own material. the renderer's _shader
    // is a serialized asset ref though, so THIS pass is the recon: inventory the
    // planes, every loaded water-ish shader, and any live renderer/shader donor.
    // the wiring lands once a raid log names the donor.
    internal static class TerminalWater
    {
        private static bool _staged;

        internal static void ResetForRaid() => _staged = false;

        internal static void TryStage()
        {
            if (_staged || !TerminalGate.On) return;
            var gw = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            if (gw == null) return;
            var planes = TerminalRigFill.FindRootNamed("Water_planes_withoutculling");
            if (!planes) return;
            _staged = true;

            try
            {
                // the recon (2026-08-18) named the state: sea planes ship with NULL
                // materials, the pump reservoir with a Cloth/ClothShader mis-bind, and
                // no SSR water pipeline exists to hand them to. so build ONE shared
                // harbor-water material on the best loaded water shader and put every
                // plane on it — flat but plausible sea beats invisible.
                Shader sh = null;
                foreach (var cand in new[] { "FX/Water4", "FX/Water", "FX/SimpleWater4", "FX/Water (Basic)" })
                {
                    var s = Shader.Find(cand);
                    if (s && s.isSupported) { sh = s; break; }
                }
                if (!sh)
                {
                    foreach (var s in Resources.FindObjectsOfTypeAll<Shader>())
                        if (s && s.isSupported && s.name.StartsWith("FX/Water")) { sh = s; break; }
                }
                if (!sh) { Plugin.Log.LogWarning("[Water] no usable water shader loaded — planes stay as shipped"); return; }

                var mat = new Material(sh) { name = "Terminal_HarborWater" };
                // murky baltic harbor — these props exist on the FX/Water family;
                // missing ones are ignored
                TrySetColor(mat, "_horizonColor", new Color(0.08f, 0.12f, 0.14f, 1f));
                TrySetColor(mat, "_baseColor", new Color(0.06f, 0.10f, 0.12f, 0.9f));
                TrySetColor(mat, "_BaseColor", new Color(0.06f, 0.10f, 0.12f, 0.9f));
                TrySetColor(mat, "_ReflectionColor", new Color(0.10f, 0.13f, 0.15f, 1f));
                TrySetColor(mat, "_Color", new Color(0.06f, 0.10f, 0.12f, 0.9f));

                int fixedN = 0;
                bool show = Plugin.WaterPlanes.Value;
                var watched = new List<Renderer>();
                void FixUnder(Transform root)
                {
                    if (!root) return;
                    foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        if (r.name.Contains("BALLISTIC")) continue;
                        r.sharedMaterial = mat;
                        r.enabled = show; // A/B lever for the port fps-tank hunt
                        watched.Add(r);
                        fixedN++;
                    }
                }
                FixUnder(planes);
                FixUnder(TerminalRigFill.FindRootNamed("Pumping_station_water_dirt"));
                FixUnder(TerminalRigFill.FindRootNamed("Pamp_Station"));
                TerminalPerfWatch.WatchWater(watched);
                Plugin.Log.LogInfo($"[Water] {fixedN} water plane(s) put on '{sh.name}' harbor material"
                    + (show ? "" : " (RENDERERS OFF — WaterPlanes config test)"));
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Water] fix failed: {e}"); }
        }

        private static void TrySetColor(Material m, string prop, Color c)
        {
            try { if (m.HasProperty(prop)) m.SetColor(prop, c); } catch { }
        }
    }
}
