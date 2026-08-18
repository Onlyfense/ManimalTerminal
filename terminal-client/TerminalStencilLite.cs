using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // STENCIL-LITE, sidecar edition (kept from the shelved tushonka experiment —
    // the one piece of that era the user wanted: outdoors brighter, indoors not,
    // per-pixel, no triggers). the authored stencil volume MESHES survive on the
    // husk GOs even though the StencilShadow scripts are dead — rows from
    // plugin-data/terminal_stencils.json name+position-match the GOs and carry
    // each volume's authored darkness (Ambient.a, 0.7-0.9 on this map).
    //
    // technique: two-sided depth-fail stencil marking (backfaces ZFail IncrWrap,
    // frontfaces ZFail DecrWrap — net nonzero = scene pixel INSIDE the closed
    // hull, camera-safe, arbitrary meshes, no through-wall bleed), then multiply
    // the deferred EMISSION gbuffer (where the flat fill lives) where marked,
    // clearing marks per-volume. LOW NIBBLE ONLY — unity's deferred lighting
    // owns the high stencil bits. pre-lighting, so lamp pools are untouched.
    // shader ships in terminal_fx.bundle (Terminal/Build FX Bundle in the SDK).
    internal static class TerminalStencilLite
    {
        private static CommandBuffer _cb;
        private static Camera _cam;
        private static Material _mat;
        private static Shader _shader;
        private static bool _shaderTried;
        private static JArray _rows;
        private static bool _rowsTried;
        private static float _appliedStrength = -1f;

        internal static void ResetForRaid() => Detach();

        internal static void Tick()
        {
            try
            {
                if (!TerminalGate.On || !Plugin.StencilDarken.Value)
                {
                    if (_cb != null) Detach();
                    return;
                }
                // cutscenes render through their own camera/light state and the
                // volumes flash over everything — wait out the intro and detach
                // for the attack cutscene window
                if ((TerminalIntroCutscene.Available && TerminalIntroCutscene.FinishedAt < 0f)
                    || TerminalAttackCutscene.PlayingNow
                    || TerminalEndingCutscene.PlayingNow)
                {
                    if (_cb != null) Detach();
                    return;
                }
                var cam = CameraClass.Instance?.Camera;
                if (!cam)
                {
                    if (_cb != null) Detach();
                    return;
                }
                // live sliders: a strength change re-attaches with the new alpha scales
                if (_cb != null && cam == _cam
                    && Mathf.Approximately(_appliedStrength,
                        Plugin.StencilDarkenIndoor.Value + Plugin.StencilDarkenOutdoor.Value * 1000f)) return;
                if (_cb != null) Detach();
                Attach(cam);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[StencilLite] tick failed: {e.Message}"); }
        }

        private static JArray StencilRows()
        {
            if (_rowsTried) return _rows;
            _rowsTried = true;
            try
            {
                var path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                    "plugin-data", "terminal_stencils.json");
                if (!System.IO.File.Exists(path))
                {
                    Plugin.Log.LogWarning("[StencilLite] terminal_stencils.json missing — no darkening");
                    return null;
                }
                var all = JObject.Parse(System.IO.File.ReadAllText(path))["rows"] as JArray;
                _rows = new JArray();
                foreach (var r in all ?? new JArray())
                    if (r.Value<string>("cls") == "StencilShadow")
                        _rows.Add(r);
                Plugin.Log.LogInfo($"[StencilLite] {_rows.Count} authored stencil volume row(s) loaded");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[StencilLite] sidecar load failed: {e.Message}"); }
            return _rows;
        }

        private static Shader DarkenShader()
        {
            if (_shaderTried) return _shader;
            _shaderTried = true;
            try
            {
                var bundle = TerminalFxBundle.Get();
                if (bundle == null)
                {
                    Plugin.Log.LogWarning("[StencilLite] no fx bundle — no darkening");
                    return null;
                }
                _shader = bundle.LoadAsset<Shader>("Assets/IcebreakerTools/Shaders/ManimalStencilDarken.shader");
                if (!_shader)
                {
                    foreach (var s in bundle.LoadAllAssets<Shader>())
                        if (s) { _shader = s; break; }
                }
                Plugin.Log.LogInfo($"[StencilLite] darken shader {(_shader ? "loaded" : "NOT FOUND in fx bundle")}");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[StencilLite] fx bundle load failed: {e.Message}"); }
            return _shader;
        }

        private static void Attach(Camera cam)
        {
            var rows = StencilRows();
            if (rows == null || rows.Count == 0) return;
            var shader = DarkenShader();
            if (!shader) return;
            if (!_mat) _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

            // name index over the terminal scenes (names survive the rip;
            // positions disambiguate reused prefabs — the TerminalStencils
            // matcher, reused)
            var wanted = new HashSet<string>();
            foreach (var r in rows) wanted.Add(r.Value<string>("go") ?? "");
            var byName = new Dictionary<string, List<Transform>>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scn = SceneManager.GetSceneAt(i);
                if (!scn.isLoaded || scn.name == null || !scn.name.StartsWith("Terminal")) continue;
                foreach (var root in scn.GetRootGameObjects())
                    Collect(root.transform, wanted, byName);
            }
            if (byName.Count == 0) return; // scenes not up yet — retry next tick

            // per-group strengths: the scene parents volumes under
            // Terminal_Stencil_Indoor / Terminal_Stencil_Outdoor — classify by
            // ancestry, fall back to the indoor value (most volumes darken interiors)
            float strengthIn = Plugin.StencilDarkenIndoor.Value;
            float strengthOut = Plugin.StencilDarkenOutdoor.Value;
            _appliedStrength = strengthIn + strengthOut * 1000f; // change key for live re-attach
            var cb = new CommandBuffer { name = "Terminal Stencil Darken" };
            // color target = emission gbuffer (flat ambient lives there); depth-
            // stencil = the camera's, for the depth-fail marking
            cb.SetRenderTarget(BuiltinRenderTextureType.GBuffer3, BuiltinRenderTextureType.CameraTarget);
            int n = 0, unmatched = 0;
            foreach (var r in rows)
            {
                var name = r.Value<string>("go") ?? "";
                var p = r["p"] as JArray;
                if (p == null || !byName.TryGetValue(name, out var cands)) { unmatched++; continue; }
                var pos = new Vector3((float)p[0], (float)p[1], (float)p[2]);
                Transform best = null;
                float bestSq = 2.0f * 2.0f;
                foreach (var c in cands)
                {
                    if (!c) continue;
                    float sq = (c.position - pos).sqrMagnitude;
                    if (sq < bestSq) { bestSq = sq; best = c; }
                }
                if (!best) { unmatched++; continue; }
                if (!best.gameObject.activeInHierarchy) continue; // authored-off volumes stay off
                var mf = best.GetComponent<MeshFilter>();
                var mesh = mf ? mf.sharedMesh : null;
                if (!mesh) continue;
                // group comes from the RETAIL authoring (sidecar 'grp', tagged from
                // level627's Terminal_Stencil_Indoor/_Outdoor) — the runtime scene
                // hierarchy is name-collision soup and can't be trusted for this
                float strength = r.Value<string>("grp") == "outdoor" ? strengthOut : strengthIn;
                float a = Mathf.Clamp01((r["f"]?["Ambient"]?.Value<float?>("a") ?? 0.8f) * strength);
                float keep = Mathf.Clamp01(1f - a);
                var mpb = new MaterialPropertyBlock();
                mpb.SetColor("_Color", new Color(keep, keep, keep, 1f));
                var trs = best.localToWorldMatrix;
                cb.DrawMesh(mesh, trs, _mat, 0, 0, mpb); // mark: backfaces, zfail incr
                cb.DrawMesh(mesh, trs, _mat, 0, 1, mpb); // unmark: frontfaces, zfail decr
                cb.DrawMesh(mesh, trs, _mat, 0, 2, mpb); // apply multiply + clear marks
                n++;
            }
            if (n == 0)
            {
                Plugin.Log.LogInfo($"[StencilLite] no volumes matched ({unmatched} unmatched) — nothing to do");
                return;
            }
            cam.AddCommandBuffer(CameraEvent.BeforeLighting, cb);
            _cb = cb;
            _cam = cam;
            Plugin.Log.LogInfo($"[StencilLite] attached: {n} authored volume(s) carve the fill in-place"
                + (unmatched > 0 ? $" ({unmatched} unmatched)" : "") + " — lamp pools untouched");
        }

        private static void Collect(Transform t, HashSet<string> wanted, Dictionary<string, List<Transform>> byName)
        {
            if (wanted.Contains(t.name))
            {
                if (!byName.TryGetValue(t.name, out var list)) byName[t.name] = list = new List<Transform>();
                list.Add(t);
            }
            for (int i = 0; i < t.childCount; i++)
                Collect(t.GetChild(i), wanted, byName);
        }

        private static void Detach()
        {
            try
            {
                if (_cam && _cb != null) _cam.RemoveCommandBuffer(CameraEvent.BeforeLighting, _cb);
            }
            catch { }
            _cb = null;
            _cam = null;
        }
    }
}
