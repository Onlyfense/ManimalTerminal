using EFT;

namespace Manimal.Terminal.Civilian
{
    // vanilla scav brain nodes drive BotDoorOpener from their own logic; our
    // custom layers replace those nodes, so without this per-tick kick the mover
    // never enters NearDoor state and civilians phase through closed doors.
    // UpdateDoorInteractionStatus is the only safe entrypoint — it has its own
    // 0.05s throttle and owns the interaction state machine (a manual
    // method_3/method_2 replication deadlocks on CurrentDoorLink, learned hard).
    internal static class CivilianDoorHelper
    {
        public static void CheckAndOpenNearbyDoor(BotOwner bot)
        {
            var opener = bot?.DoorOpener;
            if (opener == null) return;
            opener.UpdateDoorInteractionStatus();
        }
    }
}
