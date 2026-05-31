# Status And Backlog

Last updated: 2026-05-31

## Current Release State

Suitable for a source-and-overlay repository release.

This release uses:

- Patched `ZE2.ModLoader.dll`
- `ZE2.BepInExParityBridge.dll`
- `ZE2.LegacySpriteBridge/ZE2ModLoader.dll`

The game's `Data/*` folders should not be modified by XML mod loading. Mods should stay under `Mods`.

## Done

- Install/restore overlay scripts.
- BepInEx runtime overlay.
- XML mod loading.
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
- Talent XP and talent store bridge.
- No raw XML mod asset staging into `Data/*`.

## Known Risks

- Original WIP source is not included yet.
- Core loader behavior is partially controlled by a dnlib binary patcher.
- Mod manager changes require a restart to affect startup-loaded manifests.
- Broken exported map caches can still crash the game.
- Generated sprite bridge output is runtime state and should not be committed.

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

- Merge bridge/adapters into the original source once available.
- Remove binary patcher once core source implements no-staging behavior.
- Remove legacy bridge dependency once native sprite/map/talent code exists.
