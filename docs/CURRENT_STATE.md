# Current State

Last updated: 2026-05-31

## Complete

- BepInEx launch overlay.
- Installer/restore scripts.
- Patched core loader avoids staging XML assets into `Data/*`.
- Direct custom map loading from `Mods`.
- Custom map sector counts.
- Custom map tilesheets and light textures.
- Custom map shadow PNG support.
- Character, gun, bullet XML mods.
- Character, gun, bullet sprites through the bridge.
- Main menu `Mods` manager.
- Mod enable/disable.
- Mod load order state.
- Mod option state.
- Talent XP and Xbox talent store bridge.
- Optional DLL mod loading through the WIP core loader.

## Known Limits

- Original WIP BepInEx source is not currently available.
- Some functionality is implemented through adapter/bridge DLLs until the original source arrives.
- Mod manager changes generally require restart because manifests are applied at startup.
- Mod option values are stored but not automatically applied unless a mod reads them.
- Broken map caches can still crash the game. Example: a zero-byte `_Ground.bin` will produce an XNA vertex buffer error.

## Runtime Logs

Primary logs:

```text
BepInEx/LogOutput.log
Mods/ZE2ModLoader.log
```

Expected startup markers:

```text
Loading [ZE2 ModLoader 2.4.1]
Loading [ZE2 BepInEx Parity Bridge 0.1.0]
ZE2 BepInEx Parity Bridge installed.
Legacy bridge methods resolved.
Legacy sprite merge initialization invoked.
```
