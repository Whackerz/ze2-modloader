# Example Mods

Last updated: 2026-05-31

The `examples/` folder contains copyable sample mods for each supported modloader feature. These examples are intentionally small and separated by feature so authors can copy one folder, test it, then iterate.

Install an example by copying that example folder into the game's `Mods/` folder. Restart the game after adding, removing, enabling, disabling, or reordering mods.

## Example_CharacterOnly

Demonstrates:

- `Characters/*.chr`
- `Sprites/*.png`
- `<Characters>` manifest entries
- custom character sprite atlas insertion

This example uses a stock starting gun, `Pistol`, so it does not require any other example.

## Example_GunAndBullet

Demonstrates:

- `Guns/*.gun`
- `Bullets/*.bul`
- custom gun sprites
- custom bullet sprites
- `<Guns>` and `<Bullets>` manifest entries
- `SpriteMode="Horizontal"`

The example gun is named `Example Sparkcaster`, and it fires the custom bullet named `ExampleSpark`.

## Example_DirectMap

Demonstrates:

- direct map loading from `Mods`
- two-sector custom maps
- required map cache files
- `<Prefix>_Path.txt`
- `<Prefix>_Shadow.png`

The level appears as `ExampleDirectMap`. It should not copy files into `Data/Levels`.

## Example_CustomMapAssets

Demonstrates:

- direct map loading from `Mods`
- `Tilesheet="...png"`
- `LightTexture="...png"`
- automatic `<Prefix>_Shadow.png` override
- map-specific `DarkMod` and `MainOnTop`

The sample tilesheet is deliberately bright and artificial so it is obvious when the custom map texture is active.

## Example_ModOptions

Demonstrates:

- `<Options>`
- bool options
- choice options
- integer options
- in-game Mods menu option storage

Current behavior: the mod manager stores option values in `BepInEx/config/ZE2.ModManager.xml`. A gameplay DLL or future loader feature must read and apply those stored values.

## Example_ShowerForgePack

Demonstrates an all-in-one content pack:

- custom character
- multiple custom guns
- custom bullet
- character, gun, and bullet sprites
- custom character starting with a custom gun

This is the richest example, but it is intentionally less minimal than the single-feature examples.

## OptionalDllMods

Contains compiled optional DLL mod examples:

- `ZE2.EndlessPlusMod.dll` - custom game mode behavior.
- `ZE2.ProgressionRevivalMod.dll` - XP, talent points, and talent menu behavior.

DLL mods are installed under `BepInEx/plugins/Mods/` or `BepInEx/plugins/` and are meant for runtime behavior changes that XML content mods cannot express. The dedicated `Mods` subfolder is preferred.

Included source examples:

- `src/ZE2.SampleMods/ZE2.EndlessPlusMod` - custom game mode and wave behavior example.
- `src/ZE2.SampleMods/ZE2.ProgressionRevivalMod` - larger progression/menu/HUD behavior example.

See `DLL_MODS.md` for the full authoring guide.

## Recommended Testing Order

1. Install `Example_CharacterOnly` and verify the character appears.
2. Install `Example_GunAndBullet` and verify the gun appears in the gun pool/store.
3. Install `Example_DirectMap` and verify `ExampleDirectMap` appears in level select and loads.
4. Install `Example_CustomMapAssets` and verify the bright custom tilesheet appears on `ExampleAssetMap`.
5. Install `Example_ModOptions` and verify its options appear under the Mods menu.
