# ZE2 ModLoader

Current working BepInEx-based modloader for Zombie Estate 2.

This repository contains:

- `dist/overlay` - files end users copy into a local Zombie Estate 2 folder.
- `src` - current recovered/adapter source used to build the active DLLs.
- `docs` - install, mod authoring, examples, developer notes, and current status.
- `examples` - copyable example mods for each supported feature.

This repo does **not** include the Zombie Estate 2 game, game content, or Steam files. Users must own the game and install this into their own local copy.

## Current Architecture

The loader currently has three runtime pieces:

1. `ZE2.ModLoader.dll`
   - Friend's WIP BepInEx loader, patched so XML mods no longer stage raw files into `Data/*`.
   - Still handles manifest discovery, character expansion, shop/gun/bullet hooks, DLL mod loading, and some UI/runtime patches.

2. `ZE2.BepInExParityBridge.dll`
   - Companion BepInEx plugin added during source recovery.
   - Adds main-menu `Mods`, talent hooks, direct custom map loading, and level asset hooks.

3. `ZE2.LegacySpriteBridge/ZE2ModLoader.dll`
   - Rebuilt bridge from the archived loader source.
   - Handles custom sprites, direct map path resolution, custom map assets, custom shadow PNGs, and talent helpers.

When the original WIP source is available, the bridge logic should be merged into the main loader and the adapter layer can be retired.

## Install

1. Make a copy of your Zombie Estate 2 install folder.
2. Copy everything from `dist/overlay` into that copied game folder.
3. Run `Install_ZE2_ModLoader.bat` once from inside the game folder.
4. Launch with `Zombie Estate 2.exe`.
5. Check `BepInEx/LogOutput.log` for loader messages.

The installer backs up the original game executable as `Zombie Estate 2.real.exe` and places a launcher shim at `Zombie Estate 2.exe`.

## Quick Mod Folder

Put XML mods under:

```text
Mods/<ModId>/mod.xml
```

The loader also scans:

```text
BepInEx/plugins/Mods
BepInEx/plugins/ZE2.LegacySpriteBridge/Mods
```

For human-authored mods, prefer top-level `Mods`.

## Current Feature Snapshot

- Custom characters from `.chr` / `.char`
- Custom character sprites
- Custom guns from `.gun`
- Custom gun sprites
- Custom bullets from `.bul`
- Custom bullet sprites
- Custom maps loaded directly from `Mods`
- Custom map tilesheets
- Custom map light textures
- Custom map shadow PNGs named `<MapName>_Shadow.png`
- In-game main-menu `Mods` manager
- Mod enable/disable
- Mod load order state
- Basic mod-declared options state
- Talent point XP and talent store bridge
- Optional DLL mods implementing the loader extension interface

## Example Mods

The `examples/` folder includes small copyable examples:

- `Example_CharacterOnly`
- `Example_GunAndBullet`
- `Example_DirectMap`
- `Example_CustomMapAssets`
- `Example_ModOptions`
- `Example_ShowerForgePack`
- `OptionalDllMods`

See `docs/EXAMPLES.md` for what each example demonstrates and a recommended testing order.

See `docs/` for details.
