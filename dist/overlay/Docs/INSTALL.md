# Install Guide

Last updated: 2026-05-31

## Requirements

- A legally owned local copy of Zombie Estate 2.
- Windows.
- The game folder must contain `Zombie Estate 2.exe` and `steam_api.dll`.

## Fresh Install

1. Copy your Zombie Estate 2 folder somewhere safe for modding.
2. Extract the release zip.
3. Open the extracted `ZE2_ModLoader` folder.
4. Copy everything inside `ZE2_ModLoader` into the copied game folder.

Do not copy only the `.exe` or `.bat` files. The `BepInEx` folder must be copied too.

The final game folder should look like:

```text
Zombie Estate 2/
  Zombie Estate 2.exe
  Zombie Estate 2.exe.config
  steam_api.dll
  BepInEx/
    core/
      BepInEx.Preloader.Core.dll
      BepInEx.Core.dll
      0Harmony.dll
    plugins/
      ZE2.ModLoader.dll
  Mods/
  Install_ZE2_ModLoader.bat
```

5. Run:

```bat
Install_ZE2_ModLoader.bat
```

6. Launch:

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
ZE2 ModLoader parity features installed.
Integrated bridge methods resolved.
Legacy sprite merge initialization invoked.
```

If the game opens but mods do not appear, check:

```text
Mods/ZE2ModLoader.log
BepInEx/LogOutput.log
```

If the game reports that `BepInEx.Preloader.Core` could not be loaded, the install is incomplete. Copy the entire `BepInEx` folder from the release zip into the game folder and run `Install_ZE2_ModLoader.bat` again.

If `BepInEx.Preloader.Core.dll` exists but the same error still appears, make sure `Zombie Estate 2.exe.config` is beside `Zombie Estate 2.exe`. That config tells the .NET Framework launcher to probe `BepInEx/core` for BepInEx assemblies.

If the game reports `Operation is not supported` or says an assembly was loaded from a network location, Windows has blocked one or more downloaded DLLs. Right-click the zip before extracting, choose `Properties`, check `Unblock`, then extract again. The included `Zombie Estate 2.exe.config` also enables `.NET Framework` `loadFromRemoteSources` for this launcher path.

## Important Current Behavior

The current distribution is configured to avoid staging mod files into the game `Data/*` folders.

Custom maps, sprites, guns, bullets, and characters should stay in `Mods`. The runtime bridge resolves those paths directly.
