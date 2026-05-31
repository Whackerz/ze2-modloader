# ZE2 ModLoader

Current working BepInEx-based modloader for Zombie Estate 2.

This repository contains:

- `dist/overlay` - files end users copy into a local Zombie Estate 2 folder.
- `src` - current source used to build the active DLLs.
- `docs` - install, mod authoring, examples, developer notes, and current status.
- `examples` - copyable example mods for each supported feature.

This repo does **not** include the Zombie Estate 2 game, game content, or Steam files. Users must own the game and install this into their own local copy.

## Current Architecture

The loader now has one primary runtime DLL:

1. `ZE2.ModLoader.dll`
   - BepInEx plugin built from source.
   - Includes the former parity bridge hooks, mod manager UI, direct map loading, custom map assets/shadows, custom sprite handling, talents, XML mod loading, and optional DLL mod loading.
   - XML mod content is loaded from `Mods` and no longer stages raw files into game `Data/*`.

## Install

1. Make a copy of your Zombie Estate 2 install folder.
2. Extract the release zip and open the extracted `ZE2_ModLoader` folder.
3. Copy everything inside `ZE2_ModLoader` into that copied game folder, including the whole `BepInEx` folder.
4. Run `Install_ZE2_ModLoader.bat` once from inside the game folder.
5. Launch with `Zombie Estate 2.exe`.
6. Check `BepInEx/LogOutput.log` for loader messages.

The installer backs up the original game executable as `Zombie Estate 2.real.exe` and places a launcher shim at `Zombie Estate 2.exe`.

## Quick Mod Folder

Put XML mods under:

```text
Mods/<ModId>/mod.xml
```

The loader also scans:

```text
BepInEx/plugins/Mods
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
- Talent point XP and talent store support
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
