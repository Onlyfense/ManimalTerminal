---
name: icebreaker-audio-parity
description: "state + hard rules of the retail-parity audio stack (sidecars, drifted classes, dead-bus rule, boosted-wav lesson)"
metadata: 
  node_type: memory
  type: project
  originSessionId: d0a95716-c650-4f02-a997-b6b21f9e3c7e
  modified: 2026-07-31T01:04:54.989Z
---

Icebreaker's sound scene runs at near-total retail parity (audit: `docs/SOUND-PARITY-AUDIT.md`, 2026-07-30).
Two sidecars replay retail level707 onto the bundled scene: `acoustics/icebreaker_spatial_audio.json`
(630 rooms/portals/areas + per-room RoomTones) and `acoustics/icebreaker_ambient_audio.json` (343
ambient components, generic path/cls/fields records — any component class can ride it). Sound banks
rebuilt from `manimal/icebreaker_banks.bundle` + `icebreaker_sound_banks.json`.

**Hard rules learned the expensive way:**
- **Never route a source into a mixer fader nothing drives.** BSG's AmbientAudioSystem init is dead
  on this map, so master groups (AmbientOut*, Rain) rest muted; the ripped mixer asset is a dead bus
  entirely. Route only when we set the faders ourselves (GClass1174 param names + ConvertNormalizedVolumeToDB).
- **Restored player components own their AudioSources**: scene sources carry no 3D setup; spread is
  `Lerp(180,0,v)` INVERTED not degrees; rolloff curves must be ClampForever (serialized WrapMode.Loop
  wraps falloff back to full volume past max distance = "audible everywhere").
- **Play() on an inactive GO is a silent no-op**; voice loops only after the root reactivates.
- **The "deafening room tones" were a volume-boosted living_roomtone wav** in the SDK, not retail's
  authored 1.0 — byte-compare re-export against retail caught it. ZoneToneVolume knob removed;
  authored volumes play unscaled. Per-clip taste overrides live in `TasteScale()`
  (IcebreakerAmbientAudio: indoor wind + wind_howl at 0.5).
- 1.0→4.0 layout-drifted (unrestorable verbatim): HandlerPlaySound* (Door_blizzard stingers, heli
  sequence), AmbientSoundBlender, EnvironmentSoundBlendSystem, SeasonAmbientSoundDataSO, SoundBank,
  BotZone, RadioBroadcastController, LocationScene. Raw PPtr-scan recovers refs/clips from drifted
  blobs; typetree-parse with 4.0 trees works for everything else ([[eft-map-extraction]]).
