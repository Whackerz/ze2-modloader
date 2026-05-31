# Developer Notes

Last updated: 2026-05-31

## Source Layout

```text
src/ZE2.ModLoader
src/ZE2.SampleMods/ZE2.EndlessPlusMod
src/ZE2.SampleMods/ZE2.ProgressionRevivalMod
```

## Runtime Pieces

### ZE2.ModLoader.dll

This is the main BepInEx plugin and now contains the source-merged loader:

- manifest discovery and XML mod loading
- no raw XML mod asset staging into `Data/*`
- character/gun/bullet runtime loading
- sprite atlas merging
- direct custom map loading
- custom map tilesheet/light/shadow support
- main-menu `Mods` manager
- talent XP/store hooks
- optional DLL mod loading through `IZe2Mod`

The former `ZE2.BepInExParityBridge.dll`, `ZE2.LegacySpriteBridge/ZE2ModLoader.dll`, and dnlib core patcher are no longer runtime requirements.

### Sample DLL Mods

The sample DLL mods demonstrate the public `IZe2Mod` interface:

- `ZE2.EndlessPlusMod`
- `ZE2.ProgressionRevivalMod`

These are buildable source examples for gameplay DLL mods.

## Build

Run from repo root:

```powershell
dotnet build .\src\ZE2.ModLoader\ZE2.ModLoader.csproj -c Release
```

The main project expects a local game folder for compile-time references. By default it looks for:

```text
..\..\..\ze2_bepinexVersion\Zombie Estate 2\Zombie Estate 2.real.exe
```

Override the game reference path with:

```powershell
dotnet build .\src\ZE2.ModLoader\ZE2.ModLoader.csproj -c Release -p:GameRoot="C:\path\to\Zombie Estate 2"
```

Build sample DLL mods:

```powershell
dotnet build .\src\ZE2.SampleMods\ZE2.EndlessPlusMod\ZE2.EndlessPlusMod.csproj -c Release
dotnet build .\src\ZE2.SampleMods\ZE2.ProgressionRevivalMod\ZE2.ProgressionRevivalMod.csproj -c Release
```

Publish the main DLL into the overlay:

```powershell
copy .\src\ZE2.ModLoader\bin\Release\net48\ZE2.ModLoader.dll .\dist\overlay\BepInEx\plugins\ZE2.ModLoader.dll
```

## Do Not Commit

Avoid committing:

- `Zombie Estate 2.exe`
- `Zombie Estate 2.real.exe`
- `Content/`
- `Data/`
- `SaveData/`
- `Logs/`
- runtime logs
- generated `ZE2_SpriteBridge`
- generated `ZE2_MapBridge`
- `_ManagedFileBackups`
- `.bak` DLLs
- `bin/`
- `obj/`
