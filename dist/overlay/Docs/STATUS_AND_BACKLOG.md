# Status And Backlog

Last updated: 2026-05-31

## Current Release State

Suitable for a source-and-overlay repository release.

This release uses one main runtime plugin:

- `ZE2.ModLoader.dll`

The former parity bridge, legacy sprite bridge, and binary core patcher have been merged into main source or retired.

The game's `Data/*` folders should not be modified by XML mod loading. Mods should stay under `Mods`.

## Done

- Install/restore overlay scripts.
- BepInEx runtime overlay.
- Source-merged main modloader project.
- XML mod loading without raw `Data/*` staging.
- DLL mod loading.
- Custom characters, guns, bullets.
- Custom sprites through runtime atlas merge.
- Direct custom map loading from `Mods`.
- Custom map sector counts.
- Custom map tilesheets/light textures.
- Custom map `<MapName>_Shadow.png`.
- Blank custom shadow fallback.
- Main menu `Mods` manager.
- Enable/disable and load-order state.
- Mod option state.
- Talent XP and talent store support.
- Source-included optional DLL mod examples.

## Known Risks

- Mod manager changes require a restart to affect startup-loaded manifests.
- Broken exported map caches can still crash the game.
- Some source names still say `Legacy` from the bridge-era implementation.
- Generated sprite/map bridge output is runtime state and should not be committed.

## Backlog

Priority 1:

- Add strict manifest validation with clear errors.
- Add a map cache validator that rejects zero-byte `_Ground.bin` / `_Walls.bin`.
- Add a smoke test script for release overlays.
- Create a curated golden test mod pack.

Priority 2:

- Make mod option values easier for DLL/XML mods to consume.
- Add in-game diagnostics for discovered mods, map paths, and sprite atlas entries.
- Add a packaging script that rebuilds DLLs and assembles `dist/overlay`.

Priority 3:

- Rename remaining `Legacy*` internals to current names.
- Replace reflection calls to `ZE2ModLoader.ModBootstrap` with direct service calls.
- Split the large main plugin into smaller source files by feature.
