# db/

Phase 6 payload (see docs/MAP-BACKPORT-PLAYBOOK.md):

- `base.json` — retail Terminal base merged into the SPT-shaped stub (spawns,
  hostility, weather, exits). Until it exists the server mod logs a warning and
  leaves the slot dormant. Keep the stub's Id; exits' EligibleEntryPoints lowercase.
- `staticContainers.json` — generated from the BUILT bundle (gen_static_containers.py),
  regenerate after every bundle rebuild.
- `staticLoot.json` — per-container-type pools (gen_static_loot.py); locale-verify
  every container tpl.
- `looseLoot.json` — authored via SDK markers + gen_loose_loot.py.

Generators live in ManimalIcebreaker/analysis, parameterized by LEVELS_DIR + LEVELS
range (Terminal: 600–638).
