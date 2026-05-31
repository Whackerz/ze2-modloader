ZE2 ModLoader - BepInEx Overlay

Install:
1. Back up or copy your Zombie Estate 2 folder.
2. Extract/copy this overlay into that game folder.
3. Run Install_ZE2_ModLoader.bat once.
4. Launch with Zombie Estate 2.exe.

What this includes:
- BepInEx .NET Framework runtime
- ZE2.ModLoader.dll
- ZE2.BepInExParityBridge.dll
- ZE2.LegacySpriteBridge\ZE2ModLoader.dll
- installer/restore scripts
- empty top-level Mods folder scaffold

Current behavior:
- XML mods are loaded directly from Mods.
- The loader is patched to avoid copying mod files into Data/*.
- Custom maps support direct map files, custom tilesheets, and <MapName>_Shadow.png.
- The main menu includes a Mods manager for enable/disable, load order, and mod option state.

This overlay does not include the game. Use it only with your own local copy.
