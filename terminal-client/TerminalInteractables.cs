using System;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // interactable state heals, from the decompile (2026-08-10):
    //
    // CONTAINERS: GetActionsClass.smethod_16 offers "Search" ONLY when
    // DoorState == Shut — the rip left every LootableContainer at None, which
    // renders as no prompt at all (empty actions list). icebreaker's Author 11
    // rebakes _doorState=2 for exactly this reason; terminal heals it at raid
    // start instead so it works with any bundle.
    //
    // DOORS: the visible swing runs through the player-animation-driven
    // interaction; forcing InteractWithoutAnimation routes through the direct
    // SetDoorState/SmoothDoorOpenCoroutine path. doubles as the diagnostic: if
    // doors swing with this on, the animation path is what's broken on the
    // backported map; if they stay frozen, the meshes are static-batched in the
    // bundle and the build needs the unstatic pass.
    internal static class TerminalInteractables
    {
        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_HealInteractables
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!TerminalGate.On) return;
                try
                {
                    int healed = 0, remapped = 0;
                    foreach (var lc in UnityEngine.Object.FindObjectsOfType<LootableContainer>(true))
                    {
                        if (lc == null) continue;
                        // 1.0-only container tpls the SPT item db doesnt know — remap so
                        // the loot window resolves. MUST mirror gen_terminal_static_containers.py
                        // exactly or the client grid and server contents disagree.
                        switch (lc.Template)
                        {
                            case "69122598e5b8725fb10f6a93": lc.Template = "6582e6d7b14c3f72eb071420"; remapped++; break; // substation corpse -> PMC body
                            case "69122762e5b8725fb10f6b32": lc.Template = "5909d5ef86f77467974efbd8"; remapped++; break; // keycard suitcase -> weapon box 5x2
                            case "6851450d30cad62593003fa8": lc.Template = "6582e6d7b14c3f72eb071420"; remapped++; break; // RUAF corpses -> PMC body
                            case "691226272a5ee538f10d2f11": lc.Template = "578f8782245977354405a1e3"; remapped++; break; // safe_01 -> Safe
                            case "68a5b9af76a25d8e6e0e2e56": lc.Template = "68a4c0ffee0000000000cab1"; remapped++; break; // valberg private safes -> Equipment Cabinet
                            case "67a0e494e8fc6968ef0fc9da": // 1.0 generic: suitcases AND gun safes
                                lc.Template = lc.name.IndexOf("suitcase", StringComparison.OrdinalIgnoreCase) >= 0
                                    ? "5c052cea86f7746b2101e8d8"   // Plastic suitcase
                                    : "68a4c0ffee0000000000cab1";  // Equipment Cabinet (our custom 10x20)
                                remapped++;
                                break;
                            default:
                                // private suitcases author NO template at all — heal by name
                                if (string.IsNullOrWhiteSpace(lc.Template))
                                {
                                    lc.Template = lc.name.IndexOf("suitcase", StringComparison.OrdinalIgnoreCase) >= 0
                                        ? "5c052cea86f7746b2101e8d8"
                                        : "68a4c0ffee0000000000cab1";
                                    remapped++;
                                }
                                break;
                        }
                        if (lc.DoorState != EDoorState.None) continue;
                        try { lc.DoorState = EDoorState.Shut; healed++; }
                        catch { }
                    }
                    if (healed > 0 || remapped > 0)
                        Plugin.Log.LogInfo($"[Interactables] {healed} container(s) healed None -> Shut (Search prompt requires Shut), {remapped} corpse tpl(s) remapped to PMC body");

                    // DOOR TRANSFORM STOMPERS (2026-08-10: container lids swing through
                    // the exact same coroutine/CurrentAngle path, doors don't move and
                    // throw nothing) — an enabled Animator on the door hierarchy rewrites
                    // the transform every frame AFTER the door code sets it (AssetRipper
                    // habitually adds Animators to prefab roots; retail doors drive the
                    // leaf transform directly, so nothing legit is lost by disabling)
                    int stripped = 0;
                    foreach (var d in UnityEngine.Object.FindObjectsOfType<Door>(true))
                    {
                        if (d == null) continue;
                        foreach (var an in d.GetComponentsInChildren<Animator>(true))
                            if (an != null && an.enabled)
                            {
                                an.enabled = false;
                                stripped++;
                                Plugin.Log.LogInfo($"[Interactables] door '{d.name}': enabled Animator on '{an.gameObject.name}' DISABLED (transform stomper)");
                            }
                    }
                    if (stripped > 0)
                        Plugin.Log.LogInfo($"[Interactables] {stripped} door animator(s) stripped");

                    if (Plugin.InstantDoorInteract.Value)
                    {
                        int doors = 0;
                        foreach (var d in UnityEngine.Object.FindObjectsOfType<Door>(true))
                        {
                            if (d == null) continue;
                            try
                            {
                                var f = AccessTools.Field(typeof(WorldInteractiveObject), "interactWithoutAnimation");
                                if (f != null) { f.SetValue(d, true); doors++; }
                            }
                            catch { }
                        }
                        Plugin.Log.LogInfo($"[Interactables] InteractWithoutAnimation forced on {doors} door(s)");
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Interactables] heal failed: {e.Message}"); }
            }
        }

        // the door no-motion verdict machine: on every state change toward Open/Shut,
        // sample CurrentAngle now and 1.5s later. angle unchanged = the swing never ran
        // (state machine problem); angle advanced but the door looks frozen = something
        // rewrites the transform after us (animator/batching). one raid of pressing
        // Open answers which world we're in.
        [HarmonyPatch(typeof(WorldInteractiveObject), nameof(WorldInteractiveObject.SetDoorState))]
        internal static class Patch_DoorProbe
        {
            [HarmonyPostfix]
            private static void Postfix(WorldInteractiveObject __instance, EDoorState state)
            {
                if (!TerminalGate.On || !(__instance is Door)) return;
                if (state != EDoorState.Open && state != EDoorState.Shut) return;
                try
                {
                    var host = new GameObject("Terminal_DoorProbe").AddComponent<DoorProbeHost>();
                    host.Door = __instance;
                    host.Angle0 = __instance.CurrentAngle;
                    host.Target = state;
                    host.Rot0 = __instance.transform.localRotation;
                }
                catch { }
            }
        }

        internal class DoorProbeHost : MonoBehaviour
        {
            internal WorldInteractiveObject Door;
            internal float Angle0;
            internal Quaternion Rot0;
            internal EDoorState Target;
            private float _t;

            private void Update()
            {
                _t += Time.deltaTime;
                if (_t < 1.5f) return;
                try
                {
                    if (Door != null)
                    {
                        float a1 = Door.CurrentAngle;
                        float rotDelta = Quaternion.Angle(Rot0, Door.transform.localRotation);
                        Plugin.Log.LogWarning($"[DoorProbe] '{Door.name}' -> {Target}: angle {Angle0:0.#} -> {a1:0.#} "
                            + $"(open={Door.OpenAngle:0.#}) transformDelta={rotDelta:0.#}deg state={Door.DoorState} "
                            + (Mathf.Approximately(a1, Angle0)
                                ? "VERDICT: swing never ran (state machine)"
                                : (rotDelta < 1f ? "VERDICT: angle moved, transform stomped" : "moved OK")));
                    }
                }
                catch { }
                Destroy(gameObject);
            }
        }
    }
}
