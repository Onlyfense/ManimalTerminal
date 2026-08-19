using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils.Cloners;

namespace Manimal.Terminal.Server;

// SELF-CONTAINED TERMINAL (user 2026-08-19): the raid never happened, gear-wise.
// a PMC raid on terminal snapshots the full inventory at StartLocalRaid; at
// EndLocalRaid — death, extract, MIA or transit alike — the snapshot stomps
// whatever SPT's post-raid processing produced and the profile re-saves. loot
// found in-raid is discarded with everything else; gear lost comes back.
// inspired by ScrewTSW's EquipmentIsEternal (death-only), extended to every
// outcome and scoped to this map.
//
// insurance: the insured-loss pipeline is suppressed for terminal entirely
// (HandleInsuredItemLostEvent prefix) — nothing is lost, nothing returns, no
// dupes. XP/skills/quest counters from the raid are kept (only the inventory
// reverts). scav runs untouched (and the map is DisabledForScav anyway).
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 90002)]
public class TerminalSelfContained(
    ISptLogger<TerminalSelfContained> logger,
    ProfileHelper profileHelper,
    ICloner cloner,
    SaveServer saveServer) : IOnLoad
{
    // hoisted: primary-ctor captures aren't reachable from the static patch bodies
    private readonly ISptLogger<TerminalSelfContained> _log = logger;
    private readonly ProfileHelper _profiles = profileHelper;
    private readonly ICloner _cloner = cloner;
    private readonly SaveServer _saves = saveServer;

    private static bool _patched;
    private static TerminalSelfContained? _instance;

    private sealed class Snapshot
    {
        public required BotBaseInventory Inventory;
        public List<InsuredItem>? Insured;
    }

    private static readonly Dictionary<string, Snapshot> _snapshots = new();

    public Task OnLoad()
    {
        _instance = this;
        if (_patched) return Task.CompletedTask;
        _patched = true;
        var h = new Harmony("com.manimal.terminal.selfcontained");
        h.Patch(AccessTools.Method(typeof(LocationLifecycleService), nameof(LocationLifecycleService.StartLocalRaid)),
            prefix: new HarmonyMethod(typeof(TerminalSelfContained), nameof(StartPrefix)));
        h.Patch(AccessTools.Method(typeof(LocationLifecycleService), nameof(LocationLifecycleService.EndLocalRaid)),
            postfix: new HarmonyMethod(typeof(TerminalSelfContained), nameof(EndPostfix)));
        // nothing is ever lost on a self-contained map, so the insurance-lost
        // pipeline must not fire at all — it queues return mail for "lost" gear
        // BEFORE the snapshot restore runs, which would deliver as dupes later
        h.Patch(AccessTools.Method(typeof(LocationLifecycleService), "HandleInsuredItemLostEvent"),
            prefix: new HarmonyMethod(typeof(TerminalSelfContained), nameof(InsurancePrefix)));
        logger.Info("[Terminal] self-contained raids armed — gear snapshots on entry, full restore on any exit");
        return Task.CompletedTask;
    }

    public static void StartPrefix(MongoId sessionId, StartLocalRaidRequestData request)
    {
        try
        {
            var self = _instance;
            if (self is null) return;
            var key = sessionId.ToString();
            _snapshots.Remove(key); // stale snapshot from a crashed raid dies here

            if (!string.Equals(request.Location, "Terminal", StringComparison.OrdinalIgnoreCase)) return;
            var side = request.PlayerSide ?? "";
            if (!side.Contains("pmc", StringComparison.OrdinalIgnoreCase))
            {
                self._log.Info("[Terminal] scav raid — self-contained snapshot skipped");
                return;
            }

            var pmc = self._profiles.GetFullProfile(sessionId)?.CharacterData?.PmcData;
            if (pmc?.Inventory is null) return;
            _snapshots[key] = new Snapshot
            {
                Inventory = self._cloner.Clone(pmc.Inventory)!,
                Insured = self._cloner.Clone(pmc.InsuredItems),
            };
            self._log.Info($"[Terminal] gear snapshot taken ({pmc.Inventory.Items?.Count ?? 0} inventory item(s)) — this raid never happened");
        }
        catch (Exception e)
        {
            _instance?._log.Warning($"[Terminal] gear snapshot failed: {e.Message}");
        }
    }

    public static bool InsurancePrefix(string locationName)
    {
        if (string.Equals(locationName, "Terminal", StringComparison.OrdinalIgnoreCase))
        {
            _instance?._log.Info("[Terminal] insured-loss event suppressed — nothing is lost on a self-contained raid");
            return false;
        }
        return true;
    }

    public static void EndPostfix(MongoId sessionId, EndLocalRaidRequestData request)
    {
        try
        {
            var self = _instance;
            if (self is null) return;
            var key = sessionId.ToString();
            if (!_snapshots.TryGetValue(key, out var snap)) return; // not a terminal pmc raid
            _snapshots.Remove(key);

            var pmc = self._profiles.GetFullProfile(sessionId)?.CharacterData?.PmcData;
            if (pmc is null) return;
            pmc.Inventory = snap.Inventory;
            if (snap.Insured is not null) pmc.InsuredItems = snap.Insured;
            // EndLocalRaid already saved the post-raid state — save again with the
            // snapshot applied so the revert is what lands on disk
            self._saves.SaveProfileAsync(sessionId).GetAwaiter().GetResult();
            self._log.Info($"[Terminal] raid over ({request.Results?.Result}) — gear restored to the entry snapshot, as if the raid never happened");
        }
        catch (Exception e)
        {
            _instance?._log.Warning($"[Terminal] gear restore failed: {e.Message}");
        }
    }
}
