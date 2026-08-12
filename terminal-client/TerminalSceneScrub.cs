using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Manimal.Terminal
{
    // kills the LIVE-BUT-BROKEN components the guid rebind resurrected. icebreaker
    // never had this problem: its rip left these as unbound husks and the strip pass
    // removed the shells. terminal's SDK import REPAIRED their script refs (the
    // icebreaker-era stubs match by name), so they ship as real components whose
    // ripped internals NRE — first raid: ~904 frames each of TOD_Sky/WeatherController/
    // EnvironmentManager/Rain/Ripple tick spam + 906 AmbientLight null-shader ctors.
    //
    // scrub at sceneLoaded (fires after scene Awakes — those NREs are one-shot and
    // unavoidable client-side; the per-frame spam is what this stops). the weather
    // stack gets resurrected properly by the weather-system port later; until then a
    // static sky + our ambient fill carry the look. scene-name gated, never
    // TerminalGate (transit-gate-blindness).
    internal static class TerminalSceneScrub
    {
        // class name -> why it dies (ripped refs). destroyed as COMPONENTS — their
        // GOs may carry meshes (the sky dome stays, its driver goes)
        private static readonly string[] BrokenTypes =
        {
            // TOD_Sky/TOD_Time/TOD_Components NO LONGER scrubbed: terminal ships the
            // full retail sky dome rig (Sun/Moon/Atmosphere children all survived) and
            // killing the driver left an undriven blown-white dome + no sun. their
            // Awake NREs come from rip-nulled serialized PARAMETER classes — healed +
            // re-initialized by HealSky below instead.
            "EFT.Weather.WeatherController",            // LateUpdate NRE; LocalGame's weather block
                                                        // is gated on Instance != null, so its absence
                                                        // skips cleanly. resurrection = the weather
                                                        // phase (drives TOD hour/clouds; static sky
                                                        // until then)
            "EFT.Weather.ToDController",
            "RainController",
            "RippleController",
            "EFT.EnvironmentEffect.EnvironmentManager", // ripped scene instance NREs in Update —
                                                        // a bare replacement is rebuilt at Player.Init
                                                        // (TerminalRaidFixes.EnsureEnvironmentManager)
            "AmbientLight",                             // Material ctor on a null ripped shader,
                                                        // our flat ambient fill replaces it

            // the AI data holders are NO LONGER scrubbed (2026-08-10): the AI bake port
            // landed — TerminalAIBake.TryFill fills the scene holders with the retail
            // bake in the AICoversData.RestoreData prefix, and TerminalRaidFixes'
            // null-collection heal (BotsController.Init prefix) covers whatever the
            // fill misses, so RestoreData runs clean on the REAL holders.
        };

        private static Type[] _resolved;

        internal static void Scrub(Scene scene)
        {
            try
            {
                if (_resolved == null)
                {
                    var list = new List<Type>();
                    foreach (var n in BrokenTypes)
                    {
                        var t = AccessTools.TypeByName(n);
                        if (t != null && typeof(Component).IsAssignableFrom(t)) list.Add(t);
                        else Plugin.Log.LogDebug($"[Scrub] type '{n}' not found — skipped");
                    }
                    _resolved = list.ToArray();
                }

                int killed = 0;
                var counts = new Dictionary<string, int>();
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var t in _resolved)
                        foreach (var c in root.GetComponentsInChildren(t, true))
                        {
                            UnityEngine.Object.DestroyImmediate(c);
                            killed++;
                            counts.TryGetValue(t.Name, out var n);
                            counts[t.Name] = n + 1;
                        }
                if (killed > 0)
                {
                    var parts = new List<string>();
                    foreach (var kv in counts) parts.Add($"{kv.Value}x {kv.Key}");
                    Plugin.Log.LogWarning($"[Scrub] '{scene.name}': destroyed {killed} broken ripped component(s) — {string.Join(", ", parts)}");
                }

                HealSky(scene);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Scrub] '{scene.name}' failed: {e.Message}"); }
        }

        // the retail TOD sky rig survived the rip structurally (dome + all transform
        // children) but its serialized PARAMETER classes (World/Cycle/Light/...) can
        // arrive null — TOD_Sky.Awake -> Initialize -> LateUpdate -> method_18 NREs
        // and the sky sits undriven (blown-white dome, no sun). default-construct the
        // nulled classes on the TOD components, then re-run Initialize. static sky at
        // the authored hour; the weather phase later drives it live.
        private static void HealSky(Scene scene)
        {
            try
            {
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var sky in root.GetComponentsInChildren<TOD_Sky>(true))
                    {
                        int healed = 0, rewired = 0;
                        foreach (var c in sky.GetComponents<Component>())
                        {
                            if (c == null) continue;
                            var tn = c.GetType().Name;
                            if (!tn.StartsWith("TOD_")) continue;
                            healed += HealNullFields(c);
                            rewired += RewireChildRefs(c, sky.transform);
                        }
                        int assets = FillSkyResources(sky);
                        int rebound = RebindSkyShaders(sky);
                        Plugin.Log.LogWarning($"[Sky] '{scene.name}': TOD rig on '{sky.name}' — {healed} null field(s) defaulted, {rewired} child ref(s) rewired, {assets} resource asset(s) reverse-filled, {rebound} shader(s) rebound to native, re-initializing");
                        try
                        {
                            sky.Initialize();
                            Plugin.Log.LogWarning("[Sky] TOD_Sky re-initialized OK — sun/sky driven at the authored hour");
                        }
                        catch (Exception e)
                        {
                            Plugin.Log.LogError($"[Sky] TOD_Sky.Initialize still fails — sky stays undriven. FULL: {e}");
                        }
                    }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Sky] heal failed: {e.Message}"); }
        }

        // the reverse-filled materials carry the BUNDLE's recompiled TOD shaders — the
        // heli disease: they compile (isSupported lies) but render garbage. atmosphere
        // draws blown-bright whatever the sun does ("still daytime" at a synced 22:00)
        // and the sun quad loses its additive blend (opaque black-backed plane that
        // FOLLOWS the player, because TOD parents the dome to the camera). EFT ships
        // TOD natively, so the real shaders live in the game registry — force every
        // sky material onto the registry instance by name, heli-heal style.
        private static int RebindSkyShaders(TOD_Sky sky)
        {
            int rebound = 0;
            var seen = new HashSet<Material>();
            void Rebind(Material m)
            {
                if (m == null || !seen.Add(m) || m.shader == null) return;
                Shader native = null;
                try { native = GClass872.Find(m.shader.name); } catch { }
                if (native == null || !native.isSupported)
                {
                    Plugin.Log.LogWarning($"[Sky] no native shader for '{m.name}' ('{m.shader.name}') — stays on the bundle copy");
                    return;
                }
                if (ReferenceEquals(native, m.shader)) return;
                m.shader = native;
                rebound++;
                Plugin.Log.LogDebug($"[Sky] '{m.name}' rebound to native '{native.name}'");
            }

            var res = sky.GetComponent<TOD_Resources>();
            if (res != null)
            {
                Rebind(res.SpaceMaterial);
                Rebind(res.AtmosphereMaterial);
                Rebind(res.ClearMaterial);
                Rebind(res.SunMaterial);
                Rebind(res.MoonMaterial);
                Rebind(res.SkyboxMaterial);
            }
            foreach (var r in sky.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    Rebind(m);
            return rebound;
        }

        // TOD_Resources' serialized ASSETS (dome meshes + sky materials) are rip-nulled
        // and its Initialize only computes shader ids — method_0 then NREs on the null
        // Quad mesh. reverse-fill from the dome children: they still carry the authored
        // meshes/materials, and method_0's own job is assigning these exact objects
        // back onto these exact children — sourcing from them makes that a no-op.
        private static int FillSkyResources(TOD_Sky sky)
        {
            var res = sky.GetComponent<TOD_Resources>();
            if (res == null) return 0;
            Transform Child(string name)
            {
                foreach (var t in sky.transform.GetComponentsInChildren<Transform>(true))
                    if (t.name == name) return t;
                return null;
            }
            Material Mat(string name)
            {
                var t = Child(name);
                var r = t != null ? t.GetComponent<Renderer>() : null;
                return r != null ? r.sharedMaterial : null;
            }
            Mesh MeshOf(string name)
            {
                var t = Child(name);
                var mf = t != null ? t.GetComponent<MeshFilter>() : null;
                return mf != null ? mf.sharedMesh : null;
            }

            int n = 0;
            void SetMat(ref Material field, Material v) { if (field == null && v != null) { field = v; n++; } }
            void SetMesh(ref Mesh field, Mesh v) { if (field == null && v != null) { field = v; n++; } }

            SetMat(ref res.SpaceMaterial, Mat("Space"));
            SetMat(ref res.AtmosphereMaterial, Mat("Atmosphere"));
            SetMat(ref res.ClearMaterial, Mat("Clear"));
            SetMat(ref res.SunMaterial, Mat("Sun"));
            SetMat(ref res.MoonMaterial, Mat("Moon"));
            SetMat(ref res.SkyboxMaterial, Mat("Atmosphere"));

            var ico = MeshOf("Atmosphere") ?? MeshOf("Space");
            var half = MeshOf("Clear") ?? ico;
            var sph = MeshOf("Moon") ?? ico;
            var quad = MeshOf("Sun") ?? sph;
            SetMesh(ref res.IcosphereHigh, ico);
            SetMesh(ref res.IcosphereMedium, ico);
            SetMesh(ref res.IcosphereLow, ico);
            SetMesh(ref res.HalfIcosphereHigh, half);
            SetMesh(ref res.HalfIcosphereMedium, half);
            SetMesh(ref res.HalfIcosphereLow, half);
            SetMesh(ref res.SphereHigh, sph);
            SetMesh(ref res.SphereMedium, sph);
            SetMesh(ref res.SphereLow, sph);
            SetMesh(ref res.Quad, quad);
            return n;
        }

        // rip-nulled serialized GameObject/Transform refs re-pointed by NAME against
        // the rig's children (TOD_Components.Sun -> the dome's 'Sun' child, etc. —
        // Initialize null-checks these and silently skips wiring, leaving
        // SunTransform/LightTransform null for method_18 to NRE on)
        private static int RewireChildRefs(Component c, Transform rigRoot)
        {
            int n = 0;
            foreach (var f in c.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                var ft = f.FieldType;
                if (ft != typeof(GameObject) && ft != typeof(Transform)) continue;
                try
                {
                    if (f.GetValue(c) is UnityEngine.Object existing && existing != null) continue;
                    Transform match = null;
                    foreach (var t in rigRoot.GetComponentsInChildren<Transform>(true))
                        if (t.name == f.Name) { match = t; break; }
                    if (match == null) continue;
                    f.SetValue(c, ft == typeof(GameObject) ? (UnityEngine.Object)match.gameObject : match);
                    n++;
                }
                catch { }
            }
            return n;
        }

        // null plain-class fields (parameterless ctor) -> defaults; null arrays/lists
        // -> empty. never touches UnityEngine.Object refs (a defaulted material/
        // transform would hide real breakage).
        private static int HealNullFields(Component c)
        {
            int n = 0;
            foreach (var f in c.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            {
                var ft = f.FieldType;
                try
                {
                    if (f.GetValue(c) != null) continue;
                    if (typeof(UnityEngine.Object).IsAssignableFrom(ft)) continue;
                    if (ft.IsArray && ft.GetArrayRank() == 1)
                    { f.SetValue(c, Array.CreateInstance(ft.GetElementType(), 0)); n++; }
                    else if (ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(List<>))
                    { f.SetValue(c, Activator.CreateInstance(ft)); n++; }
                    else if (ft.IsClass && !ft.IsAbstract && ft.GetConstructor(Type.EmptyTypes) != null)
                    { f.SetValue(c, Activator.CreateInstance(ft)); n++; }
                }
                catch { }
            }
            return n;
        }
    }
}
