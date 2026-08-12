---
name: icebreaker-native-culling
description: verified BSG native Perfect Culling architecture + the Author 15 restore pipeline for Icebreaker
metadata: 
  node_type: memory
  type: project
  originSessionId: ca2525e7-e6a6-41a0-a980-db5da5714a78
---

BSG native culling restore (replaces the pcbake sidecar pipeline), state as of 2026-07-17: all authoring/code done, awaiting user's Author 15 run + rebuild + test raid.

Verified from decompiled assembly (D:\SPT400_assembly) + packed-file parsing:
- Packed bake `StreamingAssets/Culling_Data/<gridHash>_packed_cull.bytes` layout (NO version field): `[int32 numGuids][per guid: int32 len + bytes]...cell payloads...[offset table][int32 numCells at EOF]`, cell bounds struct = 40 bytes (center/size/rotation) + int32 payload len. Icebreaker retail 1.0 file (242MB, hash 065281ec..., 83973 cells) parses IDENTICALLY to vanilla SPT 0.16 bakes → format compatible. Copied to D:\SPTDev.
- GroupMapping = the file's 7 guids → GOs that MUST have GuidComponent + PerfectCullingCrossSceneGroup or `PerfectCullingAdaptiveGrid.method_3` THROWS and aborts raid load: 6 level708 nodes (CullingGrid, SBG_LightMeshes, SBG_Areas_01/02, SBG_Portals, SBG_Doors) + SBG_Icebreaker_Lights (level703).
- Runtime bakeGroups assembly: group.method_4 → preprocess.PrepareRuntimeContent() = concat of content-scene `PerfectCullingCrossSceneContent*._bakeGroups` sorted by _contentGroupId. Packed ushort indices point into that array BY POSITION — content arrays must be order-exact. Cross-checked: Areas_01 = Outdoor+Indoor_01+Indoor_03+Design (38786), Areas_02 = Indoor_02 (39961), LightMeshes = Lights ContentMeshes (1837), _indexSpan values confirm concat order.
- **LightGroupPreProcess trap**: retail 1.0 serializes ZERO fields for it (raw 32 bytes). Under SPT's class (inherits _cullingGroups) an empty restore would blank the Lights group at runtime — so it is deliberately NOT restored; with no preprocess the group keeps its serialized 1557 bakeGroups, which is what the bake indexes.
- Sampler/camera (PerfectCullingCrossSceneSampler) are game-side (vanilla SPT maps use them) — no map-side restore needed. PerfectCullingBakeGroup.Init null-checks renderers/cullingLightObjects → dangling refs degrade gracefully.

Pipeline artifacts:
- analysis/extract_crossscene_content.py → icebreaker_crossscene_content.json (36MB; ref TABLE format: refs = {"r": idx} into per-level [path,x,y,z] table; default-valued fields pruned — restore onto fresh components). 0 drift, 0 unresolved of 280k refs.
- SDK stubs (Assets/Scripts, Assembly-CSharp — NOT the Koenigz package asmdef): EftCullingTypes + all EFT-layer classes incl PerfectCullingCrossSceneContent{,Meshes,Doors}/CrossSceneContentPortals. sharedOccluders are GameObject[] not Renderer[].
- Author 15 (IcebreakerTools/Editor/IcebreakerNativeCulling.cs): restores level708 components (generic SerializedProperty assigner), content-scene guid anchors, and content registries (typed direct-field assign; path→Transform map with ~k sibling disambiguation + name+position fallback). Needs ALL scenes open additively.
- Bundle build + patch_icebreaker_preset.py now include Icebreaker_Culling (retail preset has the entry).
- Client stand-down in RaidFixPatches.AttachCullingCamera: if game's PerfectCullingAdaptiveGrid.Instance alive with PackedData.IsValid → sidecar driver skipped; falls back if bake didn't load.

Unverified until test raid: per-cell payload encoding 1.0-vs-0.16.9, renderer path-match rate on the AssetRipper hierarchy. If native works: retire pcbake sidecars, ghost-kill pass, drift guard; ADD Culling_Data file to the distribution zip. Related: [[icebreaker-backport]], [[icebreaker-bundle-deploy]], [[map-backport-playbook]].

**Update (July 2026):** the restored native bake COST fps vs our own sidecar bakes in practice — `NativeCulling` config (default false) now suppresses `PerfectCullingAdaptiveGrid.Awake` via Harmony prefix, and the plugin's existing fallback runs our own 6 `.pcbake` sidecar volumes (plugins/ManimalIcebreaker/culling/) instead. The restore pipeline stays valid/documented; it's just not the shipping default.
