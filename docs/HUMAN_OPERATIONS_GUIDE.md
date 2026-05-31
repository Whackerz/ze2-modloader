# Human Operations Guide

Last updated: 2026-05-31

For normal install instructions, use `INSTALL.md`.

For creating mods, use `MOD_AUTHORING.md`.

## Daily Workflow

1. Work in a copied Zombie Estate 2 folder, not the Steam install.
2. Put mods under top-level `Mods`.
3. Launch through `Zombie Estate 2.exe`.
4. Read:

```text
BepInEx/LogOutput.log
Mods/ZE2ModLoader.log
```

5. Use main menu `Mods` to inspect/toggle XML mods.
6. Restart after changing enabled mods or load order.

## Map Debugging

If a map appears but crashes on load:

- Confirm `SectorCount` matches actual sector files.
- Confirm every loaded sector has non-empty `_Ground.bin` and `_Walls.bin`.
- Confirm the prefix in `mod.xml` exactly matches file names.
- Check for `<MapName>_Shadow.png` if shadows look wrong.

Example:

```xml
<Map Prefix="Blacksite" Folder="Maps/Blacksite" SectorCount="1" />
```

Requires:

```text
Blacksite0.xml
Blacksite0_Ground.bin
Blacksite0_Walls.bin
Blacksite_Path.txt
```

## Keeping The Game Folder Clean

The current loader is patched to avoid staging XML mod files into `Data/*`.

If old staged files exist from earlier experiments, remove only files that are known mod outputs. Do not delete stock game files.
