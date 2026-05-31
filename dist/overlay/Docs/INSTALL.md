# Install Guide

Last updated: 2026-05-31

## Requirements

- A legally owned local copy of Zombie Estate 2.
- Windows.
- The game folder must contain `Zombie Estate 2.exe` and `steam_api.dll`.

## Fresh Install

1. Copy your Zombie Estate 2 folder somewhere safe for modding.
2. Copy the contents of `dist/overlay` into that copied game folder.
3. Run:

```bat
Install_ZE2_ModLoader.bat
```

4. Launch:

```bat
Zombie Estate 2.exe
```

## Example Mods

The install overlay includes example mods in:

```text
ExampleMods/
```

They are kept outside `Mods/` so they do not all load automatically. To test one, copy a single example folder from `ExampleMods/` into the top-level `Mods/` folder, restart the game, and check the Mods menu or the relevant game screen.

## What The Installer Does

The installer:

- Checks that it is inside a Zombie Estate 2 folder.
- Copies the original `Zombie Estate 2.exe` to `Zombie Estate 2.real.exe` if that backup does not already exist.
- Installs the BepInEx launcher shim as `Zombie Estate 2.exe`.

The original game executable should remain available as:

```text
Zombie Estate 2.real.exe
```

## Restore

Run:

```bat
Restore_Original_ZE2_Exe.bat
```

This restores `Zombie Estate 2.real.exe` back to `Zombie Estate 2.exe`.

## Verify

After launching, open:

```text
BepInEx/LogOutput.log
```

Expected markers:

```text
Loading [ZE2 ModLoader 2.4.1]
Loading [ZE2 BepInEx Parity Bridge 0.1.0]
ZE2 BepInEx Parity Bridge installed.
Legacy bridge methods resolved.
Legacy sprite merge initialization invoked.
```

If the game opens but mods do not appear, check:

```text
Mods/ZE2ModLoader.log
BepInEx/LogOutput.log
```

## Important Current Behavior

The current distribution is configured to avoid staging mod files into the game `Data/*` folders.

Custom maps, sprites, guns, bullets, and characters should stay in `Mods`. The runtime bridge resolves those paths directly.
