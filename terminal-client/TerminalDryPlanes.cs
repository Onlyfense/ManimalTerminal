using System;
using Comfort.Common;
using EFT;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Manimal.Terminal
{
    // RAIN OCCLUSION (user report 2026-08-18: rain falls through roofs): retail
    // covers every interior with 911 authored DryPlane quads under
    // Terminal_Scripts/Terminal_DryPlanes; WeatherObstacle.Awake combines them
    // into one mesh the rain DepthPhotograper draws and raycasts against. the rip
    // dropped the DryPlane components, so the obstacle never existed — rebuild the
    // combined mesh from the baked retail transforms (they're all builtin QUADS,
    // terminal_dryplanes.json carries world TRS per plane) and hand it to a real
    // WeatherObstacle so both native consumers just work.
    internal static class TerminalDryPlanes
    {
        private static bool _staged;

        internal static void ResetForRaid() => _staged = false;

        internal static void TryStage()
        {
            if (_staged || !TerminalGate.On) return;
            var gw = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            if (gw == null) return;
            _staged = true;

            try
            {
                var path = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".",
                    "plugin-data", "terminal_dryplanes.json");
                if (!System.IO.File.Exists(path))
                {
                    Plugin.Log.LogWarning("[DryPlanes] terminal_dryplanes.json missing — rain falls indoors");
                    return;
                }
                var planes = JObject.Parse(System.IO.File.ReadAllText(path))["planes"] as JArray;
                if (planes == null || planes.Count == 0) { Plugin.Log.LogWarning("[DryPlanes] no planes in sidecar"); return; }

                var verts = new Vector3[planes.Count * 4];
                var tris = new int[planes.Count * 12]; // both windings — raycasts come from above, draws want either face
                int vi = 0, ti = 0;
                foreach (var pTok in planes)
                {
                    var p = (JObject)pTok;
                    var pos = ReadV3(p["p"]);
                    var rot = ReadQ(p["r"]);
                    var scl = ReadV3(p["s"]);
                    // unity builtin Quad: XY plane, ±0.5
                    int b = vi;
                    verts[vi++] = pos + rot * Vector3.Scale(new Vector3(-0.5f, -0.5f, 0f), scl);
                    verts[vi++] = pos + rot * Vector3.Scale(new Vector3(0.5f, -0.5f, 0f), scl);
                    verts[vi++] = pos + rot * Vector3.Scale(new Vector3(-0.5f, 0.5f, 0f), scl);
                    verts[vi++] = pos + rot * Vector3.Scale(new Vector3(0.5f, 0.5f, 0f), scl);
                    tris[ti++] = b; tris[ti++] = b + 2; tris[ti++] = b + 1;
                    tris[ti++] = b + 2; tris[ti++] = b + 3; tris[ti++] = b + 1;
                    tris[ti++] = b; tris[ti++] = b + 1; tris[ti++] = b + 2;
                    tris[ti++] = b + 1; tris[ti++] = b + 3; tris[ti++] = b + 2;
                }
                var mesh = new Mesh { name = "Terminal_DryPlanes_Combined" };
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.vertices = verts;
                mesh.triangles = tris;
                mesh.RecalculateBounds();

                // AddComponent runs the native Awake first (combines zero DryPlane
                // children into an empty mesh) — then our mesh overwrites it. Start
                // publishes Instance next frame, exactly what the consumers poll.
                var go = new GameObject("Terminal_WeatherObstacle");
                var wo = go.AddComponent<WeatherObstacle>();
                var col = go.GetComponent<MeshCollider>();
                if (!col) col = go.AddComponent<MeshCollider>();
                col.sharedMesh = mesh;
                wo.MeshCollider = col;

                Plugin.Log.LogInfo($"[DryPlanes] rain occluder rebuilt: {planes.Count} authored quads, "
                    + $"{verts.Length} verts — indoor rain and wet-status now respect roofs");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[DryPlanes] staging failed: {e}"); }
        }

        private static Vector3 ReadV3(JToken t) => new Vector3((float)t[0], (float)t[1], (float)t[2]);
        private static Quaternion ReadQ(JToken t) => new Quaternion((float)t[0], (float)t[1], (float)t[2], (float)t[3]);
    }
}
