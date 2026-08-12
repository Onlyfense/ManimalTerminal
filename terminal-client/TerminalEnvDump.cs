using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // render-environment dumper, ported from icebreaker's RenderEnvProbe forensics.
    // the icebreaker lesson this exists for: "the difference is almost never the
    // camera itself, it's the scene's post volume + ambient" — and image-effect
    // failures are SILENT (no exception in any log, proven twice there). F8 on a
    // WORKING map, F8 on terminal, diff the two blocks.
    internal static class TerminalEnvDump
    {
        internal static void Dump(string tag)
        {
            try { DumpInner(tag); }
            catch (Exception e) { Plugin.Log.LogWarning($"[RenderEnv:{tag}] dump failed: {e.Message}"); }
        }

        private static void DumpInner(string tag)
        {
            var L = Plugin.Log;
            var loc = "<no world>";
            try { var w = Comfort.Common.Singleton<EFT.GameWorld>.Instance; if (w != null) loc = w.LocationId; } catch { }
            L.LogWarning($"===== [RenderEnv:{tag}] loc={loc} =====");

            var cam = TerminalCullingDriver.CameraRef != null ? TerminalCullingDriver.CameraRef : Camera.main;
            if (cam != null)
            {
                L.LogWarning($"[RenderEnv] camera '{cam.name}': HDR={cam.allowHDR} MSAA={cam.allowMSAA} clear={cam.clearFlags} bg={cam.backgroundColor} fov={cam.fieldOfView:F1} cullMask=0x{cam.cullingMask:X} renderingPath={cam.actualRenderingPath}");
                // IMAGE EFFECTS RUN IN COMPONENT ORDER — the first broken one eats
                // every later one. this is the actual execution order.
                var sb = new System.Text.StringBuilder();
                foreach (var c in cam.GetComponents<Component>())
                {
                    if (c == null) { sb.Append(" <missing-script>"); continue; }
                    var b = c as Behaviour;
                    sb.Append($" {c.GetType().Name}{(b != null ? (b.enabled ? "" : "(OFF)") : "")}");
                }
                L.LogWarning($"[RenderEnv] camera chain:{sb}");
            }
            else L.LogWarning("[RenderEnv] no camera!");

            L.LogWarning($"[RenderEnv] ambient: mode={RenderSettings.ambientMode} intensity={RenderSettings.ambientIntensity:F2} " +
                         $"light={RenderSettings.ambientLight} sky={RenderSettings.ambientSkyColor} eq={RenderSettings.ambientEquatorColor} gnd={RenderSettings.ambientGroundColor}");
            L.LogWarning($"[RenderEnv] skybox={(RenderSettings.skybox != null ? RenderSettings.skybox.name + "/" + (RenderSettings.skybox.shader != null ? RenderSettings.skybox.shader.name : "?") : "<null>")} " +
                         $"reflMode={RenderSettings.defaultReflectionMode} reflIntensity={RenderSettings.reflectionIntensity:F2} " +
                         $"fog={RenderSettings.fog} fogColor={RenderSettings.fogColor} fogMode={RenderSettings.fogMode} fogDensity={RenderSettings.fogDensity:F4}");

            // brightest lights — a 50-intensity anything would explain a crush
            try
            {
                var lights = UnityEngine.Object.FindObjectsOfType<Light>();
                Array.Sort(lights, (a, b) => b.intensity.CompareTo(a.intensity));
                var sb = new System.Text.StringBuilder();
                int dirCount = 0;
                foreach (var l in lights) if (l.type == LightType.Directional && l.enabled) dirCount++;
                for (int i = 0; i < lights.Length && i < 8; i++)
                    sb.Append($"{lights[i].name}({lights[i].type},on={lights[i].enabled},i={lights[i].intensity:F1}) ");
                L.LogWarning($"[RenderEnv] {lights.Length} lights ({dirCount} directional enabled), brightest: {sb}");
            }
            catch (Exception e) { L.LogWarning($"[RenderEnv] lights failed: {e.Message}"); }

            DumpPostVolumes(L);
            L.LogWarning($"===== [RenderEnv:{tag}] end =====");
        }

        private static void DumpPostVolumes(BepInEx.Logging.ManualLogSource L)
        {
            var ppvType = AccessTools.TypeByName("UnityEngine.Rendering.PostProcessing.PostProcessVolume");
            if (ppvType == null) { L.LogWarning("[RenderEnv] PostProcessVolume type not found"); return; }
            var vols = UnityEngine.Object.FindObjectsOfType(ppvType);
            L.LogWarning($"[RenderEnv] {vols.Length} PostProcessVolume(s)");
            foreach (var vol in vols)
            {
                try
                {
                    bool isGlobal = (bool)(GetMember(vol, "isGlobal") ?? false);
                    float priority = ToF(GetMember(vol, "priority"));
                    float weight = ToF(GetMember(vol, "weight"));
                    var profile = GetMember(vol, "sharedProfile") ?? GetMember(vol, "profile");
                    var profName = profile != null ? (profile as UnityEngine.Object)?.name : "<null>";
                    L.LogWarning($"[RenderEnv]  vol '{(vol as UnityEngine.Object)?.name}' global={isGlobal} prio={priority:F0} weight={weight:F2} enabled={(vol as Behaviour)?.enabled} profile={profName}");
                    if (profile == null) continue;
                    var settings = GetMember(profile, "settings") as System.Collections.IEnumerable;
                    if (settings == null) continue;
                    foreach (var s in settings)
                    {
                        if (s == null) continue;
                        bool active = (bool)(GetMember(s, "active") ?? true);
                        L.LogWarning($"[RenderEnv]    {s.GetType().Name} active={active} {DumpOverriddenParams(s)}");
                    }
                }
                catch (Exception e) { L.LogWarning($"[RenderEnv]  vol dump failed: {e.Message}"); }
            }
        }

        // read every ParameterOverride<T> field that is actually overridden, as name=value
        private static string DumpOverriddenParams(object effect)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var f in effect.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var val = f.GetValue(effect);
                if (val == null) continue;
                var ovField = f.FieldType.GetField("overrideState");
                var valField = f.FieldType.GetField("value");
                if (ovField == null || valField == null) continue; // not a ParameterOverride
                try
                {
                    if (!(bool)ovField.GetValue(val)) continue;
                    sb.Append($"{f.Name}={valField.GetValue(val)} ");
                }
                catch { }
            }
            return sb.ToString();
        }

        private static object GetMember(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanRead) return p.GetValue(o);
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return f?.GetValue(o);
        }

        private static float ToF(object o) => o is float f ? f : 0f;
    }
}
