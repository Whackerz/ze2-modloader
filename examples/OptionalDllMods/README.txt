Optional DLL Mods

ZE2.EndlessPlusMod.dll is an example compiled BepInEx/plugin-side mod.
Install optional DLL mods by placing them in:

BepInEx/plugins/Mods/

The loader scans BepInEx/plugins/Mods/*.dll for classes that implement:

ZE2.ModLoader.IZe2Mod

Included source examples:

src/ZE2.SampleMods/ZE2.EndlessPlusMod
src/ZE2.SampleMods/ZE2.ProgressionRevivalMod

See docs/DLL_MODS.md for the full DLL mod authoring guide.
