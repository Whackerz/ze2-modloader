# AI Runbook

Last updated: 2026-05-31

## Mission

Maintain the current BepInEx ZE2 modloader until the original WIP source becomes available.

## Key Source Folders

```text
src/ZE2.BepInExParityBridge
src/ZE2.BepInExCorePatcher
src/ZE2.LegacySpriteBridge
```

## Runtime Components

```text
BepInEx/plugins/ZE2.ModLoader.dll
BepInEx/plugins/ZE2.BepInExParityBridge.dll
BepInEx/plugins/ZE2.LegacySpriteBridge/ZE2ModLoader.dll
```

## Important Design Constraint

Do not restore behavior that copies XML mod files into the game's `Data/*` folders. The current goal is direct loading from `Mods`.

## Current Direct Map Path

`ZE2.BepInExParityBridge` patches `Level.ThreadLoad`.

It calls methods on `ZE2ModLoader.ModBootstrap`:

- `HasCustomMap`
- `GetSectorCount`
- `ResolveLevelPath`
- `ApplyLevelAssets`

`ZE2.LegacySpriteBridge` owns direct mod root discovery and path resolution.

## Shadow PNG Behavior

`ApplyLevelAssets(levelName)` searches:

1. `Levels/<MapName>_Shadow.png`
2. `Data/Levels/<MapName>_Shadow.png`
3. direct mod map folder

If no shadow PNG exists for a custom map, it assigns a blank transparent `512x512` texture.

## Core Binary Patch

`ZE2.BepInExCorePatcher` patches the compiled WIP core loader:

- `Plugin.ApplyMaps` -> return `0`
- `Plugin.ApplyRawFile` -> return `0`

This prevents raw XML mod staging.

Run:

```powershell
dotnet run --project .\src\ZE2.BepInExCorePatcher\ZE2.BepInExCorePatcher.csproj -- ".\path\to\BepInEx\plugins\ZE2.ModLoader.dll"
```

## Common Failure Signatures

`Resource size must be greater than zero` during `Sector.LoadSectorVertices`:

- Usually empty `_Ground.bin` or `_Walls.bin`.
- Check map export/cache files.

Custom map missing:

- Check `mod.xml` enabled state.
- Check prefix/file name casing.
- Check `Mods/ZE2ModLoader.log`.

Wrong shadows:

- Add `<MapName>_Shadow.png`.
- Confirm it is 512x512.

## Merge Plan Later

When original WIP source arrives:

1. Move parity bridge patches into main loader source.
2. Move legacy bridge direct loaders into main loader services.
3. Replace reflection calls with direct calls.
4. Remove binary patcher.
5. Remove legacy bridge DLL once all behavior is native.
