# AI Runbook

Last updated: 2026-05-31

## Mission

Maintain the source-merged BepInEx ZE2 modloader.

## Key Source Folders

```text
src/ZE2.ModLoader
src/ZE2.SampleMods/ZE2.EndlessPlusMod
src/ZE2.SampleMods/ZE2.ProgressionRevivalMod
```

## Runtime Components

```text
BepInEx/plugins/ZE2.ModLoader.dll
```

The former parity bridge, legacy sprite bridge, and core binary patcher are retired.

## Important Design Constraint

Do not restore behavior that copies XML mod files into the game's `Data/*` folders. The current goal is direct loading from `Mods`.

## Current Direct Map Path

`src/ZE2.ModLoader/ParityFeatures.cs` patches `Level.ThreadLoad`.

It calls integrated `ZE2ModLoader.ModBootstrap` methods:

- `HasCustomMap`
- `GetSectorCount`
- `ResolveLevelPath`
- `ApplyLevelAssets`

`src/ZE2.ModLoader/ModBootstrap.cs` owns direct mod root discovery and path resolution.

## Shadow PNG Behavior

`ApplyLevelAssets(levelName)` searches:

1. `Levels/<MapName>_Shadow.png`
2. `Data/Levels/<MapName>_Shadow.png`
3. direct mod map folder

If no shadow PNG exists for a custom map, it assigns a blank transparent `512x512` texture.

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

## Next Cleanup Targets

1. Rename remaining `Legacy*` internal symbols.
2. Replace reflection calls to `ZE2ModLoader.ModBootstrap` with direct service calls.
3. Split the large main plugin source into feature files.
4. Add strict manifest and map cache validation.
