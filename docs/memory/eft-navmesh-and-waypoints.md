---
name: eft-navmesh-and-waypoints
description: "How EFT bot pathing works (CalculatePath, never NavMeshAgent) and what DrakiaXYZ-Waypoints actually loads"
metadata: 
  node_type: memory
  type: reference
  originSessionId: b1787f9b-5533-4ba4-8c9b-c814257b830a
---

Verified 2026-07-02 (decompiled assembly + Waypoints plugin on disk):

- Tarkov maps ship a baked Unity navmesh; BSG bots path on it via `NavMesh.CalculatePath`/`SamplePosition` (`BotMover.cs`, `BotPathFinderClass.cs` in D:\SPT400_assembly).
- **BSG never uses `NavMeshAgent`** — zero references in the bot movement stack. Bots are moved manually along calculated corners. In practice a `NavMeshAgent` added by a mod sat completely dead in raid (no movement, no errors) — use CalculatePath + manual corner-following instead.
- DrakiaXYZ-Waypoints ships per-map `navmesh\<map>-navmesh.bundle` files (plugins\DrakiaXYZ-Waypoints) and loads them via `NavMesh.AddNavMeshData` (confirmed by DLL strings) — it EXTENDS the game's baked mesh, it is not the source of it. Standard NavMesh queries see the union.
- Both path endpoints must be projected onto the mesh with `SamplePosition` first or `CalculatePath` returns nothing (player standing on props/stairs).

Applied in `hideoutcat\RaidCompanionFollower.cs` (raid companion phase 2) — corner-following like the hideout `CatGraphTraverser`, same Thrust/Turn animator contract. See [[dialoguekit-architecture]] for the companion system context.
