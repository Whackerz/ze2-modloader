# Current State

Last updated: 2026-05-31

## Complete

- BepInEx launch overlay.
- Installer/restore scripts.
- Main `ZE2.ModLoader.dll` is now built from source.
- Former parity bridge and legacy sprite/map/talent bridge behavior is integrated into the main loader source.
- XML mods avoid raw `Data/*` staging; content should remain in `Mods`.
- Direct custom map loading from `Mods`.
- Custom map sector counts, tilesheets, light textures, and `<MapName>_Shadow.png`.
- Character, gun, bullet XML mods.
- Character, gun, bullet sprite merging.
- Main menu `Mods` manager.
- Mod enable/disable, load order state, and option state.
- Talent XP and Xbox talent store support.
- Optional DLL mod loading through `IZe2Mod`.
- Multiplayer lobby mod signature publishing/filtering for clients with matching mod sets.
- Source-included optional DLL mod examples.

## Known Limits

- Mod manager changes generally require restart because manifests are applied at startup.
- Mod option values are stored but not automatically applied unless a mod reads them.
- Broken map caches can still crash the game. Example: a zero-byte `_Ground.bin` will produce an XNA vertex buffer error.
- Some older internal names still say `Legacy` because the integrated source was merged from the bridge-era implementation.

## Runtime Logs

Primary logs:

```text
BepInEx/LogOutput.log
Mods/ZE2ModLoader.log
```

Expected startup markers:

```text
Loading [ZE2 ModLoader 2.4.1]
ZE2 ModLoader parity features installed.
Integrated bridge methods resolved.
Legacy sprite merge initialization invoked.
```
