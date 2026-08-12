---
name: icebreaker-loot-pipeline
description: loose-loot authoring round trip — scene markers are the source of truth; the live-1.0 merge is preserved via Author 24 + PoolPoint + LIVE_INJECT
metadata: 
  node_type: memory
  type: project
  originSessionId: d0a95716-c650-4f02-a997-b6b21f9e3c7e
  modified: 2026-08-03T08:52:31.944Z
---

Loose-loot pipeline (verified 2026-08-03): Unity `IcebreakerLooseLootSpot` markers → Author 12 export (`analysis/icebreaker_loose_spots.json`) → `analysis/gen_loose_loot.py` → `icebreaker-server/db/looseLoot.json`. The live-1.0 loot merge (2026-08-02, done directly in the json) was made regen-proof three ways:

- **Marker `PoolPoint` bool** — an OverrideTpl spot that emits as a budgeted pool spawnpoint (`icelive_*`) instead of forced. Export writes `poolPoint`; generator has a matching branch.
- **`LIVE_INJECT` table in gen_loose_loot.py** — the 28 live-injected items with FINAL weights, appended AFTER price tilt (do not move into EXTRA_ITEMS — tilt would double-apply). Also `MEAN_OVERRIDE = 258.0` (live's measured avg; sum-of-probs would give ~280).
- **Author 24 (`IcebreakerLiveLootImport.cs`)** — imports `analysis/live_loot_markers.json` (built by `gen_live_loot_markers.py` from shipped json) into the scene: deletes replaced markers (old flare/engine/C-1 spots by tpl), joins live's extra crew-keycard + torch positions onto the existing `crewcard`/`torch` marker groups, creates the rest under `LiveLootImport`. Idempotent by marker name.

Round trip verified by simulation: regen matches shipped exactly (46 forced, 493 pool, all distributions/groups/probs). Torch markers keep the VANILLA tpl (67ab3d4b...) — `TPL_REPLACE` swaps to the usable clone (9a449693...) at gen time. Keycard policy: engine room + C-1 = live spawns verbatim; crew = ours + live alternates (7-position group); torch = ours + BSG alternates (5-position group, ONE torch per raid). Related: [[icebreaker-quest-authoring]], [[ai-data-dumper-project]].
