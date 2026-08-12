---
name: eft-map-extraction
description: "DrakiaXYZ's map extraction workflow + the level-file-to-map mapping; EFT maps are built-in level### scene files, each map has a dedicated _AI scene layer"
metadata: 
  node_type: memory
  type: reference
  originSessionId: 87f2486e-d24b-416d-914c-c082d0bca226
---

Two DrakiaXYZ gists (fetched 2026-07-04):
- Extraction guide: https://gist.github.com/DrakiaXYZ/b828010db2252f5ed34c4c5c839e4ccc — select ONLY the target map's `level###` files in `EscapeFromTarkov_Data\` and drag them together into AssetRipper → ExportedProject → delete AnimationClip/AudioClip/OcclusionCullingData/Plugins/Scripts/VideoClip folders → open in Unity Hub → drag all scenes into hierarchy → run his EFTCleaner.cs (Setup Layers / Show Missing Terrain / Show Missing Objects). This is the answer to "AssetRipper only has Export All Files": load only one map's files and export-all IS selective export.
- Level mapping: https://gist.github.com/DrakiaXYZ/0c392b4ad6781f287e3f281bdc79e70e — per-map level lists (e.g. factory4_day = levels 525-538 → Assets/Content/Locations/Factory_Rework/*.unity; Sandbox = 465-510; Labyrinth = 544-557). Numbering CHANGES per game version — his SPT-MapInfoExtractor tool regenerates the list from game files.

MapInfoExtractor is NOT needed (it requires old AssetRipper 0.3.4.0): wrote `Desktop\ManimalAIDataDumper\analysis\extract_scene_list.py` (UnityPy, reads BuildSettings from globalgamemanagers; scene index == level file number) — works on any version/install. EFT 1.0 live install (`C:\Battlestate Games\Escape from Tarkov`) mapping saved to `analysis\eft_1.0_level_map.txt`: 710 scenes, 763 loose level files, notable 1.0 additions Icebreaker (levels 698-709), Terminal (600-689 range), Venders; Factory_Rework 525-542, Sandbox 465-510+512, Labyrinth 544-557. 1.0 is IL2CPP (il2cpp_data folder) — its Assembly-CSharp is NOT decompilable like SPT's Mono client. StreamingAssets\Windows\maps has per-map `*_preset.bundle` files. The user's mystery "kdv_a_sn" scene (from an earlier AssetRipper session showing bundle "data.unity3d") exists in NEITHER install's build list nor 1.0 StreamingAssets — unknown origin.

Implications for the custom-map plan (see [[ai-data-dumper-project]]):
- EFT maps are BUILT-IN Unity scenes (multi-scene additive: geometry districts + _AI + Light + Sound layers per map), not StreamingAssets bundles. Each map has a dedicated `*_AI.unity` scene — thats where the BotZone/covers/voxel data we reverse-engineered lives.
- Custom-map loading question is now narrowed: how does the game resolve a location id → list of scenes to load, and can that be pointed at scenes from an asset bundle. Traceable in the decompile (LocationSettingsClass scene fields / raid scene-loading flow) — next research step.
