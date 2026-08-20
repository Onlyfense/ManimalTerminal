Manimal-Terminal — EFT 1.0's Terminal map, backported to SPT 4.0
=================================================================

EARLY RELEASE. It's playable start to finish (checkpoint intro, gear
confiscation, the port, the endgame evac and the ending cutscene), but it is
under active development and rough edges remain. Report issues with your
BepInEx/LogOutput.log attached.

REQUIREMENTS (install these first)
----------------------------------
- SPT 4.0.13
- WTT-ServerCommonLib + WTT-ContentBackport
- DrakiaXYZ-BigBrain
- MoreBotsAPI (plugin + prepatch + MoreBotsServer)
- BlackDiv (TacticalToaster)
- RUAF Come Home (TacticalToaster)

INSTALL
-------
Extract the zip over your SPT install root (the folder with SPT.Server.exe /
EscapeFromTarkov.exe). It ships two trees:
  BepInEx\plugins\ManimalTerminal\   (client plugin + map bundles + data)
  BepInEx\patchers\ManimalTerminalPrepatch\
  SPT\user\mods\ManimalTerminal\     (server mod + map db)

Nothing is written under EscapeFromTarkov_Data.

THE MAP
-------
Terminal is reached via direct map select. Whatever happens,
your inventory is restored to its pre-raid state afterwards — gear
lost comes back, loot found is left behind. Insurance never triggers. Scav
runs are disabled.

KNOWN ISSUES (early release)
----------------------------
- Frame timing degrades gradually over long raids (under investigation)
- Second raid in one game session had visual/state bugs — fixed recently,
  but restart the game between raids if anything looks off
- Ending cutscene polish ongoing (subtitles/timing may drift per voice)

CREDITS
-------
- BSG for the map and everything ripped from retail 1.0
- Tactical Toaster (MoreBotsAPI/RUAF/BlackDiv), DrakiaXYZ (BigBrain),
  WTT team (CommonLib/ContentBackport), Koenigz (Perfect Culling)
- ScrewTSW's EquipmentIsEternal for the self-contained-gear inspiration
