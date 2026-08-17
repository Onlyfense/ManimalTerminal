using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using UnityEngine;

namespace Manimal.Terminal
{
    // LOOT VISIBILITY, ported from icebreaker 08-13. two separate things live here:
    //
    // 1. SKINNED-MESH BOUNDS. a SkinnedMeshRenderer only refreshes its bounds while it
    //    is on screen, which is exactly the trap — it culls itself and then never
    //    updates again, so the item "disappears at certain angles". updateWhenOffscreen
    //    is the standard Unity fix and is cheap at loot mesh sizes. this one is a real
    //    bug and applies whatever the LOD settings are.
    //
    // 2. RADIUS CULLING. LodBiasClamp is a GLOBAL multiplier on every LOD transition AND
    //    cull distance. terminal ships 1.0 against BSG's authored floor of 2.0, so loot
    //    starts vanishing at roughly half the intended distance — "invisible until I get
    //    close". rather than exempt loot from culling entirely (which on icebreaker cost
    //    measurable fps: hundreds of loot models rendering to subpixel size), loot is
    //    exempted from the LOD cull and then deleted outright past LootCullRadius. inside
    //    the radius it renders at EVERY range with no fade or dither; outside it is gone.
    //    a hard cutoff draws strictly less than subpixel geometry, so this is both
    //    cheaper and tunable.
    //
    // positions are read LIVE from the transform, never cached: loot moves (dropped,
    // kicked, spilled out of containers), and a cached-position culler eats moving
    // objects — on icebreaker that bug culled the player's melee out of his hands.
    internal static class TerminalLootBounds
    {
        private const float SweepEvery = 4f;
        private const float LootCullHeight = 0.0004f; // small enough to survive any sane bias
        private const int ChecksPerFrame = 64;
        private const float ShowHysteresis = 1.06f;   // hide at R, show again at R*1.06

        private sealed class Tracked
        {
            public LootItem Item;
            public Renderer[] Rends;
            public bool Hidden;
        }

        private static readonly HashSet<int> _done = new HashSet<int>();
        private static readonly List<Tracked> _tracked = new List<Tracked>();
        private static float _next;
        private static int _cursor, _fixedRenderers, _lodFixed;
        private static bool _radiusMode, _summarised;

        internal static void ResetForRaid()
        {
            UnhideAll();
            _done.Clear();
            _next = 0f; _cursor = 0; _fixedRenderers = 0; _lodFixed = 0;
            _radiusMode = false; _summarised = false;
        }

        internal static void Tick()
        {
            TickRadiusCull();

            if (Time.time < _next) return;
            _next = Time.time + SweepEvery;

            // flipping LootCullRadius across 0 changes which items need the LOD exemption,
            // so re-walk everything once when the mode changes
            bool wantRadius = Plugin.LootCullRadius.Value > 0f;
            if (wantRadius != _radiusMode)
            {
                _radiusMode = wantRadius;
                _done.Clear();
                if (!wantRadius) UnhideAll();
                Plugin.Log.LogInfo($"[LootVis] loot cull radius {(wantRadius ? Plugin.LootCullRadius.Value.ToString("0") + "m" : "OFF (LOD bias governs loot again)")}");
            }

            var loot = Singleton<GameWorld>.Instance?.LootItems;
            if (loot == null) return;

            for (int i = 0; i < loot.Count; i++)
            {
                LootItem item;
                try { item = loot.GetByIndex(i); }
                catch { continue; }
                if (item == null) continue;
                if (!_done.Add(item.GetInstanceID())) continue;
                try { Fix(item); }
                catch (Exception e) { Plugin.Log.LogDebug($"[LootVis] '{item.name}' failed: {e.Message}"); }
            }

            if (!_summarised && _done.Count > 0 && Time.time > 30f)
            {
                _summarised = true;
                Plugin.Log.LogInfo($"[LootVis] {_done.Count} loot item(s) checked — {_lodFixed} LODGroup cull height(s) lowered "
                    + $"(compensating lodBias {QualitySettings.lodBias:0.00}), {_fixedRenderers} skinned renderer(s) set to update offscreen"
                    + (_radiusMode ? $", culling past {Plugin.LootCullRadius.Value:0}m" : ""));
            }
        }

        private static void Fix(LootItem item)
        {
            if (_radiusMode)
            {
                foreach (var g in item.GetComponentsInChildren<LODGroup>(true))
                {
                    if (g == null) continue;
                    var lods = g.GetLODs();
                    if (lods == null || lods.Length == 0) continue;
                    int last = lods.Length - 1;
                    if (lods[last].screenRelativeTransitionHeight <= LootCullHeight) continue;
                    lods[last].screenRelativeTransitionHeight = LootCullHeight;
                    g.SetLODs(lods);
                    _lodFixed++;
                }

                var rends = item.GetComponentsInChildren<Renderer>(true);
                if (rends != null && rends.Length > 0)
                    _tracked.Add(new Tracked { Item = item, Rends = rends, Hidden = false });
            }

            foreach (var r in item.GetComponentsInChildren<Renderer>(true))
            {
                if (r is SkinnedMeshRenderer smr && !smr.updateWhenOffscreen)
                {
                    smr.updateWhenOffscreen = true;
                    _fixedRenderers++;
                }
            }
        }

        // flat per-frame cost: a fixed slice of the list, one squared distance each, and a
        // renderer write ONLY on a boundary crossing. forceRenderingOff rather than .enabled
        // so we never fight whatever else owns the renderer's enabled flag.
        private static void TickRadiusCull()
        {
            if (!_radiusMode || _tracked.Count == 0) return;
            var cam = TerminalCullingDriver.CameraRef;
            if (cam == null) return;

            float r = Plugin.LootCullRadius.Value;
            float hideSq = r * r;
            float showSq = (r * ShowHysteresis) * (r * ShowHysteresis);
            var camPos = cam.transform.position;

            int n = Mathf.Min(ChecksPerFrame, _tracked.Count);
            for (int k = 0; k < n; k++)
            {
                if (_cursor >= _tracked.Count) _cursor = 0;
                var t = _tracked[_cursor];

                // picked up / destroyed — drop it. nothing to restore: the renderers went
                // with the object, and an item that becomes inventory is no longer a LootItem
                if (t == null || t.Item == null) { _tracked.RemoveAt(_cursor); continue; }
                _cursor++;

                float d = (t.Item.transform.position - camPos).sqrMagnitude;
                bool hide = t.Hidden ? d > showSq : d > hideSq; // hysteresis, no strobing
                if (hide == t.Hidden) continue;

                t.Hidden = hide;
                for (int i = 0; i < t.Rends.Length; i++)
                    if (t.Rends[i] != null) t.Rends[i].forceRenderingOff = hide;
            }
        }

        private static void UnhideAll()
        {
            foreach (var t in _tracked)
            {
                if (t?.Rends == null) continue;
                for (int i = 0; i < t.Rends.Length; i++)
                    if (t.Rends[i] != null) t.Rends[i].forceRenderingOff = false;
                t.Hidden = false;
            }
            _tracked.Clear();
            _cursor = 0;
        }
    }
}
