using System.Collections.Generic;
using System.Reflection;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using Manimal.Terminal.Civilian.Behavior;
using SPT.Reflection.Patching;

namespace Manimal.Terminal.Civilian.Patches
{
    // registers the two civilian layers with BigBrain at boot. the brain list is
    // a shotgun because MoreBotsAPI swaps the base brain at activation and the
    // resulting ShortName isnt predictable — extras are free, the layers only
    // bind to our WildSpawnType int.
    internal sealed class CivilianBrainInitPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(TarkovApplication).GetMethod(
                nameof(TarkovApplication.Init),
                BindingFlags.Public | BindingFlags.Instance);
        }

        [PatchPostfix]
        private static void PatchPostfix()
        {
            var brains = new List<string>
            {
                "Assault",
                "AssaultGroup",
                "CursedAssault",
                "ExUsec",
                "PMC",
                "PmcBEAR",
                "PmcUSEC",
                "PmcBear",
                "PmcUsec",
            };
            var types = new List<WildSpawnType>
            {
                (WildSpawnType)CivilianConstants.WildSpawnTypeValue,
            };

            // passive baseline (always on) + flee — the retail 1.0 civilian brain
            // shape (avoid-danger + hide). priorities 90/95: a first raid at 55/65
            // had one civilian SHOOT the player, so some vanilla combat layer
            // outranks the 60s — sit above everything the assault brain owns
            BrainManager.AddCustomLayer(typeof(CivilianPassiveLayer), brains, 90, types);
            BrainManager.AddCustomLayer(typeof(CivilianFleeLayer), brains, 95, types);

            BepInEx.Logging.Logger.CreateLogSource("Terminal.Civ")
                .LogInfo("Civilian BigBrain layers registered (passive 90, flee 95)");
        }
    }
}
