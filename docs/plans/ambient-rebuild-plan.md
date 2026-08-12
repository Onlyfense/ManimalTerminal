# PLAN: Rebuild Terminal's ambient audio from retail data

Handoff plan (written 2026-08-11 by Fable session, for Opus execution). Goal: replace the
hand-assembled ambient approximations with retail 1.0's AUTHORED ambient system —
banks, volumes, intervals, splines — rebuilt at runtime from extracted level data, the
same resurrection pattern that already works for spatial audio on this map.

## Why (context you must not re-litigate)

- The map's ambient beds currently come from `TerminalSoundRig` (client) — hand-built
  banks in `terminal-client/plugin-data/sound_rig_banks.json` with guessed groupings and
  volumes. That's how `amb_sparrow_in_garbage_02` and a shovel-scrape ended up in the
  `far_shoots` distant-firefight bank, playing globally at firefight volume (user's
  "rat squeak + chain jingle" complaint — fixed by eviction, but the class of bug
  remains as long as banks are hand-authored).
- Retail's ambient authoring SURVIVED in the level binaries and has already been
  extracted (see below). The user's directive: "the whole time we shouldve been
  building the sound banks off retail data with volumes and splines etc."
- KEEP `TerminalSoundRig`'s non-ambient roles: cutscene phase machine, DoorOpenBeat,
  sirens, FirefightRunner. Only its ambient-bed overlap is superseded (see step 7).

## What already exists (verified this session — do not rebuild)

**Extraction pipeline**: `C:\Users\peard\Desktop\TerminalLevels\analysis\extract_terminal_spatial_audio.py`
(param-ported from icebreaker; UnityPy + typetrees generated from
`D:\SPTDev\EscapeFromTarkov_Data\Managed`, `TTH.read_typetree_boost = None` mandatory).
Output: `terminal_spatial_audio.json` (37MB), shipped at
`terminal-client\plugin-data\acoustics\terminal_spatial_audio.json` (PostBuild deploys it).

**Already extracted, clean** (scenes.Terminal_Sound, from `level636`):
- AmbientSoundPlayer x92, LoopAmbientSoundPlayer x99, OneShotAmbientSoundPlayer x71
- SoundPoint x430, SoundPointsManager x49, AmbientSoundPlayerGroup x28
- SoundPlayerRandomPointComponent x68, SoundPlayerSplineTrigger x71
- AmbientPlayerSplineMappedEmitter x58, BezierSpline x65, spline movers/controllers
- SoundAmbientZoneCalculator x41, SoundPlayerRoomObserverComponent x30
- AmbientAudioSystem x1, EnvironmentSoundBlendSystem x1
- PLUS the whole spatial tier (rooms/portals/areas) — already staged and working at
  runtime via `TerminalAcoustics.cs`; don't touch it.

**Extracted with PARSE ERRORS (1.0 layout drift — see step 6)**:
- SeasonLoopAmbientSoundPlayer 107 of 122 failed, SeasonAmbientSoundPlayer 39 of 41,
  EventRandomPlayer 16/16, EventLoopPlayer 2/2, EventSoundBankChanger 6/6,
  WeatherRandomAmbientSoundPlayer 3/19. Rows carry `parse_error`.

**Player row fields** (all clean classes share this shape):
`_volume, _deltaVolume, _randomPitchRange{x,y}, _fadeInDurationSec, _fadeOutDurationSec,
_mixerGroup(ref), ForceSetMixerGroup, _playOnAwake, _spread, _spatialBlend,
_minDistance, _maxDistance, _rolloffCurve, _startPlayingDelay, _spreadCurve,
_useCustomSpreadCurve` + per-class: `_randomTimeRange{x,y}` (AmbientSoundPlayer),
`_loopClip(ref)` (Loop), `_soundBank(ref)` (OneShot), `_ambientBank(ref)` (base).
Ground truth example: `MetalSqueaksMiddleRPP` = AmbientSoundPlayer, `_volume 0.15`,
`_playOnAwake 0`, `_spatialBlend 1`.

**Client staging infra** (`terminal-client/TerminalAcoustics.cs`) — reuse, don't reinvent:
`Sidecar()` (loads plugin-data/acoustics json), `BuildGoIndex()/FindGo()` (path + local-pos
disambiguation), `CreateAll<T>()` (component onto deactivated GO → fill → reactivate),
`FillFields()/ConvertToken()/Curve()/V3()/Q4()` (json→object reflection filler),
`FindClip()` (bundle AudioClip by name, per-raid cache), `Rows()`, `Ref()` (path_id →
created Component). The SpatialAudioSystem singleton resurrection trick
(`OnSceneLoaded`: AddComponent on the authored scene-root GO so the game's own init
path finds it Instantiated) is the template for AmbientAudioSystem.

**Interim heals to SUPERSEDE** (in TerminalAcoustics): `TryApplyAmbientAuthoring()` +
`AmbientGovernor` — applies authored volumes and fake random re-triggers. Once the real
players run, this must stand down (step 7).

## The missing piece: SoundBank assets

The players reference bank/clip ASSETS via `{"externalRef": [fileIdx, pathId]}` — these
are ScriptableObject/asset objects in level636's EXTERNAL files, not scene objects.
Verified externals order for level636 (PPtr m_FileID is 1-based; externals[0] is
fileID 1): 1=globalgamemanagers.assets, 2=resources.assets, 3=sharedassets67.assets,
4=Library/unity default resources, 5=sharedassets636.assets, 6=sharedassets397.assets,
7=sharedassets400.assets, 8=sharedassets628.assets, 9=sharedassets13.assets,
10=sharedassets537.assets. All these files exist in `C:\Users\peard\Desktop\TerminalLevels\`
EXCEPT sharedassets397/400 (verify with ls; 397 was absent for the occlusion asset —
banks likely live in sharedassets636, the scene's own sharedassets, which EXISTS).
A census confirmed level636 itself holds no bank assets (only EventSoundBankChanger
components + a SoundBankPreloader).

Field TYPES to confirm from decompile before extracting:
`D:\SPT400_assembly\Assembly-CSharp\Audio\AmbientSubsystem\AmbientSoundPlayer.cs` —
what class is `_ambientBank` (likely `SoundBank` or `AmbientSoundBank`), and its asset
field layout. `TerminalSoundRig.cs` already rebuilds SoundBank instances via reflection
from json — read it for the field names it sets; reuse that mechanism.

## Steps

### 1. Extend the extractor to resolve bank + clip refs to NAMES
In `extract_terminal_spatial_audio.py`:
- For `_loopClip` refs: resolve the PPtr into the external file's AudioClip object and
  emit `"_loopClipName": "<clip name>"` on the row. The extractor already does exactly
  this for room tones (`RoomToneName`) — replicate that code path.
- For `_ambientBank`/`_soundBank` refs: load the external assets file
  (`UnityPy.load(globalgamemanagers.assets, <external>)`), read the bank MonoBehaviour
  with a typetree from the 4.0 assembly (same `gen.get_nodes_up` flow), and emit a new
  top-level sidecar section `"banks": { "<bankKey>": { name, fields..., clipNames: [...] } }`
  where bankKey = `"{fileIdx}:{pathId}"`; stamp rows with `"_bankKey"`. Resolve every
  clip PPtr inside the bank to its NAME (AudioClip objects in the same/other externals).
- If a bank's typetree read fails, hexdump-compare one instance and add a surgery like
  `door_surgery` (drift is usually 1-2 fields; document what you find in the script
  header comment like the existing notes).
- Rerun; copy the json to `terminal-client\plugin-data\acoustics\` (PowerShell Copy-Item;
  build deploys it).

### 2. Clip availability audit
- Collect every referenced clip name (loop clips + bank clips). Check against the
  BUNDLE's clips: at runtime `FindClip` does this, but pre-audit offline against
  `C:\Users\peard\Desktop\TerminalLevels\audio_export\` (exported retail wavs) and the
  SDK's Author-26 holder (`TerminalSoundRigHolder.cs` — the pattern for shipping clips
  in the bundle). Emit the missing-from-bundle list.
- For missing clips: extend the Author 26 holder clip list (SDK editor script) so the
  user's next bundle rebuild carries them. DO NOT block the runtime rebuild on this —
  bind what exists, log a shopping list for the rest (the room-tone pattern:
  `_roomToneMisses`).

### 3. Rebuild bank assets at runtime
In TerminalAcoustics (new section, or a new `TerminalAmbient.cs` if cleaner):
- For each sidecar `banks` entry: create the bank instance
  (`ScriptableObject.CreateInstance(bankType)` if it's a ScriptableObject — CHECK the
  decompile; if it's a plain [Serializable] class, `Activator.CreateInstance`),
  `FillFields` the plain values, bind clips via `FindClip`. Cache by bankKey.
- Reuse/merge with TerminalSoundRig's existing reflection SoundBank builder if it's the
  same type — one builder, two consumers.

### 4. Rehydrate the ambient components
Staged exactly like the spatial tier (pass 1 create on deactivated GOs → pass 2 fill →
pass 3 reactivate), same `comps` dict so cross-refs resolve:
- Creation order matters: BezierSpline first (emitters reference splines), then
  SoundPoint/SoundPointsManager, then players
  (AmbientSoundPlayer/Loop/OneShot + WeatherRandom clean rows), then group/controller
  classes (AmbientSoundPlayerGroup, SoundPlayerRandomPointComponent — its only field is
  `_worldSoundPoints` refs, SoundPlayerSplineTrigger, AmbientPlayerSplineMappedEmitter,
  SplineEmitterPathMover, SoundAmbientZoneCalculator, SoundPlayerRoomObserverComponent,
  AmbientSoundPlayerGroupController, AmbientSplineEmitterController, SplineTriggerChecker,
  AmbientPlayerAutoPanner, PrecipitationAmbientBlender, AmbientSoundBlender), and
  EnvironmentSoundBlendSystem last.
- Fill via FillFields + manual wiring for refs: `_loopClipName`→FindClip,
  `_bankKey`→bank cache, component refs via `Ref(comps, ...)`,
  `_mixerGroup` → leave null v1 (default mixer routing; a later pass can map to the
  game's AudioMixerGroups by name via Resources.FindObjectsOfTypeAll<AudioMixerGroup>).
- `_rolloffCurve`/`_spreadCurve` via `Curve()`.
- SKIP rows with `parse_error` (season/event variants) in v1 — see step 6.

### 5. AmbientAudioSystem resurrection + init
- The system component is rip-hollowed like SpatialAudioSystem was. Same trick: in
  `TerminalAcoustics.OnSceneLoaded` (already registered for Terminal_Sound), also
  ensure the AmbientAudioSystem component exists (find its authored host GO path from
  the sidecar row's `go`; census says exactly 1 instance).
- WHO CALLS `Initialize`: verified callers are in `EFT/NetworkGame.cs` (lines ~702, ~799:
  `MonoBehaviourSingleton<AmbientAudioSystem>.Instance.Initialize()` guarded on
  `Instantiated`/`Initialized`). CHECK whether BaseLocalGame/LocalGame has an equivalent
  call for offline raids (grep). If not, call `Initialize()` ourselves after the
  rehydration pass (guard on `!Initialized`). Read AmbientAudioSystem.Initialize in the
  decompile FIRST to learn what it enumerates (registry vs FindObjectsOfType — if it
  enumerates via LocationScene registries, the players may need registry wiring; if
  FindObjectsOfType, rehydration order alone suffices).
- The 4.0 class may also need its own settings asset — read its fields; the sidecar's
  AmbientAudioSystem row carries the authored values (FillFields them before Init).

### 6. Season/Event player drift surgery (stretch, do LAST)
122 SeasonLoopAmbientSoundPlayer + 41 SeasonAmbient are a big share of the soundscape.
Their failure is 1.0 drift in (probably) the SeasonSoundPreset-ish struct. Method that
worked for doors/LC: dump raw bytes of ONE failing instance
(`o.get_raw_data()`), parse alongside the 4.0 typetree flat list, find where alignment
breaks (a value that stops making sense), and add drop_field/insert_after surgery.
Season gating itself (which season is active) may not even matter on this map — if the
preset resolves to per-season clip sets, v1 can flatten to the default/winter set.

### 7. Stand down the overlapping approximations
- `TryApplyAmbientAuthoring` + `AmbientGovernor`: gate behind
  `if (<full ambient staged>) return;` — keep as fallback when staging fails.
- `sound_rig_banks.json` beds that duplicate retail ambience (debris, far_shoots
  arena clips are FINE — they're battle-phase fiction, not map ambience; leave the rig's
  phase machine alone). Only remove rig pieces if double-audio is actually heard —
  test first, delete second, and note anything removed in the playbook doc.
- Config: `AmbientRetail` (Terminal section, default true) gating the whole tier;
  reuse `Plugin.SpatialAudio` gate style. Wire `Patch()` registrations in Plugin.cs
  following the existing pattern (configs near the top, patches in the Patch() block).

### 8. Verify
- Build: `dotnet build terminal-client\terminal-client.csproj` (PostBuild deploys DLL +
  plugin-data to D:\SPTDev\BepInEx\plugins\ManimalTerminal). If deploy fails with a
  file-lock error the GAME IS RUNNING — tell the user, don't fight it.
- Raid log expectations (grep -a, the log has binary bytes):
  `[Acoustics] ambient tier staged: N players, M banks (K clips missing)` (your new
  line), the existing `SPATIAL AUDIO STAGED`, no NRE storms from Audio.AmbientSubsystem.
- In-game: squeaks/birds positional and quiet (MetalSqueaks at 0.15 near their metal),
  no global 2D blasting. The user's F11 SoundProbe (TerminalLights.DumpNearPlayerSounds)
  names any offender — but NOTE its blind spot found 2026-08-11: it distance-cuts at
  60m using transform distance, so 2D sources far from their transform escape it.
  Improving it to always include spatialBlend<0.5 sources (marked GLOBAL) is a
  worthwhile side fix.
- Update `docs/memory/terminal-backport.md` with what landed (append-style, dated,
  match the existing entries' voice).

## Gotchas (paid for in blood, this session and icebreaker's)

- `TTH.read_typetree_boost = None` or the patched trees crash the boost reader.
- Unity fake-null: liveness-track built objects with a marker GO in the Sound scene
  (dies with the raid → next raid rebuilds). See `_spatialMarker`/`_ambientMarker`.
- Raid-start `AudioSettings.Reset()` STOPS every playing source (icebreaker lesson) —
  BSG's own AmbientAudioSystem restarts its players, which is one more reason to init
  the real system instead of Play()ing sources by hand.
- Components must be created on DEACTIVATED GOs and filled before reactivation (Awake
  reads fields).
- GO paths in Terminal_Sound are NOT unique — always disambiguate by local position
  (FindGo does this).
- The registry-wiring regression class (see playbook doc): if anything you rehydrate
  is also consumed via LocationScene registries or engine init paths that run BEFORE
  your fill, the one-shot `Init`/`_isInited` latches eat your wiring silently. Grep
  `GetAllObjects<T>` for every class you resurrect.
- Server restart only needed for db changes — this phase is client-only.
- The user's editor loop (Author 32 → 29 → bundle rebuild) is SEPARATE pending work;
  don't entangle this phase with it, EXCEPT if you extend the Author-26 clip holder
  (step 2) — then tell the user to rerun Author 26 + rebuild bundle for missing clips.

## Definition of done

1. Sidecar carries banks with clip names + volumes; rows link to them.
2. Ambient tier stages at raid start: retail players live with authored
   volumes/curves/banks; AmbientAudioSystem initialized without errors.
3. Interim governors/authoring stand down automatically.
4. Missing-clip shopping list logged once per raid; Author 26 extended for them.
5. Playbook doc updated; user told exactly what (if anything) to run in the editor.
