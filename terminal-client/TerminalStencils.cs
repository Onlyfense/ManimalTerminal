using System;
using System.Collections.Generic;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // THE AMBIENT STENCIL SYSTEM (user find 2026-08-16: the Terminal_Stencil tree +
    // ambient portals, Missing Mono Script on every one). retail's indoor/outdoor
    // ambient masking: StencilShadow meshes mark volumes in the stencil buffer,
    // AnalyticSource portals describe the light openings, and the AmbientLight screen
    // pass consumes both via STATIC registries (AmbientLight.AddStencilShadow — so
    // restore order vs the pass doesn't matter). this is what stops raised ambient
    // from cooking interiors like the armory; the user explicitly chose this proper
    // port over a player-position damping workaround ("outdoor stencils exist too").
    //
    // rows are position+name-keyed from the dump (88 StencilShadow, 80 AnalyticSource
    // across 15 levels — most stencils ride prop prefabs, not just the Portals scene).
    // components self-register in Awake/OnEnable once added and filled.
    internal static class TerminalStencils
    {
        private static JObject _sidecar;
        private static bool _sidecarTried;
        private static bool _staged;

        internal static bool Staged => _staged;

        internal static void ResetForRaid() => _staged = false;

        private static JObject Sidecar()
        {
            if (_sidecarTried) return _sidecar;
            _sidecarTried = true;
            try
            {
                var path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                    "plugin-data", "terminal_stencils.json");
                if (System.IO.File.Exists(path)) _sidecar = JObject.Parse(System.IO.File.ReadAllText(path));
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Stencils] sidecar parse failed: {e.Message}"); }
            return _sidecar;
        }

        public static bool TryStage()
        {
            if (_staged) return true;
            if (!Plugin.AmbientStencil.Value) return false;
            var rows = Sidecar()?["rows"] as JArray;
            if (rows == null || !TerminalLoaded.Check()) return false;

            try
            {
                // candidate index: every transform in a Terminal scene whose GO name
                // appears in the row set (names survive the rip; positions disambiguate
                // reused prefabs)
                var wanted = new HashSet<string>();
                foreach (var r in rows) wanted.Add(r.Value<string>("go") ?? "");
                var byName = new Dictionary<string, List<Transform>>();
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scn = SceneManager.GetSceneAt(i);
                    if (!scn.isLoaded || !scn.name.StartsWith("Terminal")) continue;
                    foreach (var root in scn.GetRootGameObjects())
                        Collect(root.transform, wanted, byName);
                }
                if (byName.Count == 0) return false; // scenes not up yet — retry later

                var tShadow = AccessTools.TypeByName("StencilShadow");
                var tSource = AccessTools.TypeByName("AnalyticSource");
                if (tShadow == null || tSource == null)
                {
                    Plugin.Log.LogWarning("[Stencils] StencilShadow/AnalyticSource types missing from 4.0 — system off");
                    _staged = true;
                    return true;
                }

                int shadows = 0, sources = 0, unmatched = 0;
                foreach (var r in rows)
                {
                    var name = r.Value<string>("go") ?? "";
                    var cls = r.Value<string>("cls") ?? "";
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

                    var type = cls == "StencilShadow" ? tShadow : tSource;
                    Component comp = best.gameObject.GetComponent(type);
                    if (!comp)
                    {
                        // fill BEFORE Awake registers: add on an inactive GO only when
                        // it already is; live GOs get filled right after AddComponent —
                        // registration reads Renderer/bounds, not our fields, so a
                        // post-Awake fill is safe here
                        try { comp = best.gameObject.AddComponent(type); }
                        catch (Exception e) { Plugin.Log.LogWarning($"[Stencils] {cls} on '{name}': {e.Message}"); continue; }
                    }
                    if (!comp) continue;
                    if (r["f"] is JObject f) Fill(comp, f);
                    if (cls == "StencilShadow") shadows++; else sources++;
                }

                _staged = true;
                Plugin.Log.LogWarning($"[Stencils] AMBIENT STENCIL SYSTEM RESTORED: {shadows} stencil shadow(s), "
                    + $"{sources} analytic portal(s){(unmatched > 0 ? $", {unmatched} row(s) unmatched" : "")} — "
                    + "AmbientLight's registries are fed; interiors mask sky ambient the retail way");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Stencils] restore failed: {e}");
                return false;
            }
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

        // typed filler for the two classes' plain fields (refs were dropped at
        // extraction — Awake resolves Renderer/MeshFilter from the GO itself)
        private static void Fill(Component target, JObject fields)
        {
            var type = target.GetType();
            foreach (var prop in fields.Properties())
            {
                var fi = AccessTools.Field(type, prop.Name);
                if (fi == null || prop.Value.Type == JTokenType.Null) continue;
                try
                {
                    if (fi.FieldType == typeof(float)) fi.SetValue(target, prop.Value.Value<float>());
                    else if (fi.FieldType == typeof(int)) fi.SetValue(target, prop.Value.Value<int>());
                    else if (fi.FieldType == typeof(bool))
                        fi.SetValue(target, prop.Value.Type == JTokenType.Boolean ? prop.Value.Value<bool>() : prop.Value.Value<int>() != 0);
                    else if (fi.FieldType.IsEnum) fi.SetValue(target, Enum.ToObject(fi.FieldType, prop.Value.Value<long>()));
                    else if (fi.FieldType == typeof(Color) && prop.Value is JObject c)
                        fi.SetValue(target, new Color(c.Value<float>("r"), c.Value<float>("g"), c.Value<float>("b"), c.Value<float>("a")));
                    else if (fi.FieldType == typeof(Vector3) && prop.Value is JObject v)
                        fi.SetValue(target, new Vector3(v.Value<float>("x"), v.Value<float>("y"), v.Value<float>("z")));
                }
                catch { }
            }
        }
    }
}
