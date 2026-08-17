# ManimalTerminal

Backport of the retail 1.0 **Terminal** map (the endgame port map) into SPT 4.x.
Same backport/fixup/restore approach as ManimalIcebreaker, which is the working
reference implementation — keep a copy at `..\ManimalIcebreaker` and read its code
instead of reinventing systems.

## Read first

1. `docs/MAP-BACKPORT-PLAYBOOK.md` — the full backport guide (phases, traps,
   performance playbook). Follow it phase by phase; run a raid after each phase.
2. `docs/memory/terminal-backport.md` — Terminal-specific progress snapshot
   (staging, export, SDK import, cutscene chain state). Point-in-time — verify
   claims against the actual files before relying on them.
3. `docs/memory/` — the other snapshots are Icebreaker-era deep-dives (bot AI,
   navmesh, culling, LOD/fps, audio parity, loot pipeline, fika, firewalls).
   Treat as archaeology with timestamps; the playbook is the corrected layer.

## Key facts

- Terminal is a FIRST-CLASS dormant slot on SPT's Locations record (own Id
  "Terminal", mongo 5704e5a4d2720bb45b8b4567) — no Suburbs-style hijack or locale
  rebrand. Shoreline's vanilla transit SHO_TRANSIT_25 already targets it.
- Retail levels 600–637 + 650 (block 2 = 651–689, the port-event states, not
  staged). Staging: `C:\Users\peard\Desktop\TerminalLevels`. No .resS siblings —
  art lives in shared containers.
- Extraction/generator scripts live in `..\ManimalIcebreaker\analysis`,
  parameterized by LEVELS_DIR + LEVELS range. Terminal-specific tooling is in
  `TerminalLevels\analysis`.
- Dev SPT install: `D:\SPTDev` (client deploy: `BepInEx\plugins\ManimalTerminal`,
  server deploy: `SPT\user\mods\ManimalTerminal`). Fika test install: `D:\SPTDevFika`.

## Conventions

- `Directory.Build.props` is the single source of truth for version/guid;
  `BuildInfo.g.cs` is generated at compile time — never hardcode either.
- Client: `com.manimal.terminal` / `Manimal-Terminal` (Forge naming rules).
- Attach Harmony patches per-class in isolated try/catch — one
  AmbiguousMatchException kills a whole PatchAll batch.
- Build Release for deploys.


## Code style

- `var` when the right-hand side names the type or makes it unambiguous;
  explicit type when C# requires it (null-init/late-assign locals), when
  reflection erases it (`GetValue`/`GetComponent(Type)` results), or when the
  type itself is information the reader needs. Necessity over uniformity.
- Unity null checks: `if (obj)` / `if (!obj)` on UnityEngine.Object types (the
  `==` overload silently degrades to reference equality on erased static types);
  no `?.`/`??` on Unity types; null refs after Destroy. NOTE: EFT's bot
  subsystems (BotMover/BotSteering/BotCoversData/BotMemory — the GClass429
  family) are PLAIN classes, `?.` is fine there; BotOwner/Player/BotZone/
  LoddedSkin and friends are Components — truthiness checks only.
- No LINQ in terminal-client (game extension methods shadow it — removing
  `using System.Linq` surfaced `GClass1518.Distinct` with a different signature
  at compile time, the shadow is REAL; allocation discipline for per-frame
  paths). Server-side LINQ is fine. Legacy files still carrying LINQ
  (AIBake/Acoustics/AudioFixes/CameraDonor/Flares/GearTax/RaidFixes/SceneScrub)
  are load-time-only paths — sweep them opportunistically when touched, not as
  churn.
