using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Manimal.Terminal
{
    // PORT SECURITY GEAR CONFISCATION (user spec 2026-08-10): entering terminal takes
    // your headgear (helmet/headset/facecover/glasses), every weapon incl melee, your
    // backpack, and everything carried INSIDE the gear you keep (rig, pockets, and the
    // pack'n'strap belt when present). the rig, armor vest and belt stay equipped.
    // secured container is never touched.
    //
    // "perfect exact copies" by construction: the ORIGINAL Item objects are moved —
    // ids, durability, attachments and loaded ammo ride along untouched — into ONE
    // random equipment cabinet (gun safe), whose generated loot is cleared first.
    // safe is picked BEFORE anything is taken: no cabinet bound in the scene = no
    // confiscation, so gear can never be stranded in limbo.
    internal static class TerminalGearTax
    {
        private const string CabinetTpl = "68a4c0ffee0000000000cab1";

        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.OnGameStarted))]
        internal static class Patch_GearTax
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!TerminalGate.On || !Plugin.GearConfiscation.Value) return;
                new GameObject("Terminal_GearTax").AddComponent<TaxHost>();
            }
        }

        internal class TaxHost : MonoBehaviour
        {
            private void Start() => StartCoroutine(Run());

            private IEnumerator Run()
            {
                // let loot bind + the player settle; the intro cutscene covers this
                yield return new WaitForSeconds(2f);
                var player = Singleton<GameWorld>.Instance?.MainPlayer;
                float deadline = Time.time + 15f;
                while (player == null && Time.time < deadline)
                {
                    yield return new WaitForSeconds(0.5f);
                    player = Singleton<GameWorld>.Instance?.MainPlayer;
                }
                if (player == null) { Done("no main player"); yield break; }

                // the held weapon must leave the hands before it leaves the inventory
                try { player.SetEmptyHands(null); } catch { }
                yield return new WaitForSeconds(0.6f);

                try { Confiscate(player); }
                catch (Exception e) { Plugin.Log.LogWarning($"[GearTax] confiscation failed: {e}"); }
                Destroy(gameObject);
            }

            private void Confiscate(Player player)
            {
                var eq = player.Profile?.Inventory?.Equipment;
                if (eq == null) { Done("no equipment"); return; }

                // valberg safes ONLY (user call 2026-08-10, no fallback) — retail's
                // PrivateLootableContainer family, authored for exactly this. none
                // bound = no confiscation.
                var safes = UnityEngine.Object.FindObjectsOfType<LootableContainer>()
                    .Where(l => l != null && l.ItemOwner != null
                        && (l.name.IndexOf("valberg", StringComparison.OrdinalIgnoreCase) >= 0
                            || (l.transform.parent != null && l.transform.parent.name.IndexOf("valberg", StringComparison.OrdinalIgnoreCase) >= 0)))
                    .ToList();
                if (safes.Count == 0) { Done("no bound valberg safe in scene — gear untouched"); return; }
                var safe = safes[UnityEngine.Random.Range(0, safes.Count)];
                var root = safe.ItemOwner.RootItem as CompoundItem;
                if (root?.Grids == null || root.Grids.Length == 0) { Done($"cabinet '{safe.name}' has no grids — gear untouched"); return; }

                var taken = new List<Item>();

                // whole-slot confiscation: head + weapons + backpack
                EquipmentSlot[] slots =
                {
                    EquipmentSlot.Headwear, EquipmentSlot.Earpiece, EquipmentSlot.FaceCover, EquipmentSlot.Eyewear,
                    EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster,
                    EquipmentSlot.Scabbard, EquipmentSlot.Backpack,
                };
                foreach (var sl in slots)
                {
                    var slot = eq.GetSlot(sl);
                    var it = slot?.ContainedItem;
                    if (it == null) continue;
                    var res = slot.RemoveItem(false);
                    if (res.Failed) { Plugin.Log.LogWarning($"[GearTax] couldnt take {sl} '{it.LocalizedName()}': {res.Error}"); continue; }
                    taken.Add(it);
                }

                // contents-only: rig + pockets stay equipped, their cargo doesnt.
                // pack'n'strap belt found by slot id — a modded slot, not in the enum.
                var hosts = new List<CompoundItem>();
                if (eq.GetSlot(EquipmentSlot.TacticalVest)?.ContainedItem is CompoundItem rig) hosts.Add(rig);
                if (eq.GetSlot(EquipmentSlot.Pockets)?.ContainedItem is CompoundItem pockets) hosts.Add(pockets);
                foreach (var s in eq.GetAllSlots())
                    if ((s.ID ?? "").IndexOf("belt", StringComparison.OrdinalIgnoreCase) >= 0
                        && s.ContainedItem is CompoundItem belt && !hosts.Contains(belt))
                        hosts.Add(belt);

                foreach (var host in hosts)
                {
                    if (host.Grids == null) continue;
                    foreach (var grid in host.Grids)
                        foreach (var it in grid.Items.ToList())
                        {
                            var res = grid.RemoveWithoutRestrictions(it);
                            if (res.Failed) { Plugin.Log.LogWarning($"[GearTax] couldnt take '{it.LocalizedName()}' from {host.LocalizedName()}: {res.Error}"); continue; }
                            taken.Add(it);
                        }
                }

                if (taken.Count == 0) { Done("nothing to confiscate"); return; }

                // out with the cabinet's generated loot, in with the player's gear —
                // big items first so the packer never strands a rifle behind a bandage
                foreach (var grid in root.Grids)
                    grid.RemoveAll();

                int placed = 0;
                foreach (var it in taken.OrderByDescending(CellArea))
                {
                    bool ok = false;
                    foreach (var grid in root.Grids)
                    {
                        var loc = grid.FindFreeSpace(it);
                        if (loc == null) continue;
                        var res = grid.Add(it, loc, false);
                        if (res.Failed) continue;
                        ok = true;
                        placed++;
                        break;
                    }
                    if (!ok) Plugin.Log.LogError($"[GearTax] NO ROOM in cabinet for '{it.LocalizedName()}' — item stranded (report this)");
                }

                Plugin.Log.LogInfo($"[GearTax] confiscated {taken.Count} item(s) -> '{safe.name}' ({placed} placed) at {safe.transform.position}");
                try
                {
                    NotificationManagerClass.DisplayMessageNotification(
                        "Port security has confiscated your equipment. It is locked in one of the terminal's equipment cabinets.",
                        ENotificationDurationType.Long, ENotificationIconType.Alert, Color.yellow);
                }
                catch { }
            }

            private static int CellArea(Item it)
            {
                try { var s = it.CalculateCellSize(); return s.X * s.Y; }
                catch { return 1; }
            }

            private void Done(string why)
            {
                Plugin.Log.LogInfo($"[GearTax] {why}");
                Destroy(gameObject);
            }
        }
    }
}
