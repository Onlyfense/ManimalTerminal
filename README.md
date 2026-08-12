# ManimalTerminal

Backport of the retail Escape from Tarkov **Terminal** map into SPT 4.x —
scene restore, component recovery, and gameplay fixup, following the approach
proven on [ManimalIcebreaker](../ManimalIcebreaker).

- `terminal-client/` — BepInEx client plugin (`Manimal-Terminal`)
- `terminal-server/` — SPT server mod (binds the native Terminal location slot)
- `docs/` — the map-backport playbook + working-notes snapshots

Status: project scaffold. Unity-side work (staging, AssetRipper export, SDK
import, cutscene rebuild) is further along — see `docs/memory/terminal-backport.md`.
