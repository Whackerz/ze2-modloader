# Mod Authoring Guide

Last updated: 2026-05-31

## Recommended Layout

```text
Mods/
  MyCoolMod/
    mod.xml
    Characters/
    Guns/
    Bullets/
    Sprites/
    Maps/
      MyMap/
        MyMap0.xml
        MyMap0_Ground.bin
        MyMap0_Ground.xml
        MyMap0_Walls.bin
        MyMap0_Walls.xml
        MyMap_Path.txt
        MyMap_Shadow.png
```

For multi-sector maps, include `MyMap1.*` and optionally `MyMap2.*`, then set `SectorCount`.

## Basic Manifest

```xml
<Ze2Mod>
  <Id>my_cool_mod</Id>
  <Name>My Cool Mod</Name>
  <Enabled>true</Enabled>

  <Characters>
    <Character File="Characters/Ada.chr" Sprite="Sprites/Ada.png" />
  </Characters>

  <Guns>
    <Gun File="Guns/Byte Blaster.gun" Sprite="Sprites/ByteBlasterGun.png" />
  </Guns>

  <Bullets>
    <Bullet File="Bullets/ByteBolt.bul" Sprite="Sprites/ByteBolt.png" SpriteMode="Horizontal" />
  </Bullets>

  <Maps>
    <Map Prefix="MyMap" Folder="Maps/MyMap" SectorCount="1" />
  </Maps>
</Ze2Mod>
```

## Copyable Examples

The repository includes focused sample mods in `examples/`:

```text
Example_CharacterOnly
Example_GunAndBullet
Example_DirectMap
Example_CustomMapAssets
Example_ModOptions
Example_ShowerForgePack
OptionalDllMods
```

See `EXAMPLES.md` for the feature demonstrated by each folder. These examples are meant to be copied into the game's top-level `Mods` folder one at a time while learning.

## Custom Maps

Map prefixes should be simple and safe:

```text
Letters, numbers, underscore, dash
No spaces
```

Required files for a one-sector map:

```text
MyMap0.xml
MyMap0_Ground.bin
MyMap0_Walls.bin
MyMap_Path.txt
```

Recommended debug/export files:

```text
MyMap0_Ground.xml
MyMap0_Walls.xml
```

The game uses the `.bin` files during normal play. If a `.bin` is empty, the game can crash while creating vertex buffers.

## Custom Map Shadow PNG

The standalone map editor can export:

```text
MyMap_Shadow.png
```

It should be `512x512` and match the 32x32 tile map at 16 pixels per tile.

Search order:

1. `Levels/MyMap_Shadow.png`
2. `Data/Levels/MyMap_Shadow.png`
3. The direct mod map folder, e.g. `Mods/MyCoolMod/Maps/MyMap/MyMap_Shadow.png`

For normal mods, put it beside the map files in the mod map folder.

If no external shadow exists for a custom map, the loader creates a blank transparent `512x512` shadow texture so the map does not inherit built-in level shadows.

## Custom Map Tilesheets

Maps may declare custom map textures:

```xml
<Map Prefix="MyMap"
     Folder="Maps/MyMap"
     SectorCount="1"
     Tilesheet="Maps/MyMap/MyMapTiles.png"
     LightTexture="Maps/MyMap/MyMapLight.png" />
```

Aliases for `Tilesheet`:

```text
Texture
AssetSheet
```

PNG and XNB texture paths are supported by the bridge. PNG is preferred for mod folders.

## Character Sprites

Character sprites should be a `64x16` strip:

```text
4 cells wide
16x16 per cell
```

The loader attempts to normalize source images, but clean 64x16 strips are safest.

## Gun Sprites

Gun sprites should be a `128x16` strip:

```text
8 cells wide
16x16 per cell
```

## Bullet Sprites

Bullet sprites can be single cells or small strips. Use `SpriteMode` when needed:

```xml
<Bullet File="Bullets/Needle.bul" Sprite="Sprites/Needle.png" SpriteMode="Horizontal" />
```

Supported modes include:

```text
Horizontal
Vertical
Pair
Both
```

## Mod Options

Mods may expose options for the in-game `Mods` menu:

```xml
<Options>
  <Option Key="HardMode" Name="Hard Mode" Type="Bool" Default="false" />
  <Option Key="SpawnRate" Name="Spawn Rate" Type="Choice" Default="Normal" Choices="Low|Normal|High" />
  <Option Key="BonusLives" Name="Bonus Lives" Type="Int" Default="1" />
</Options>
```

Option values are stored in:

```text
BepInEx/config/ZE2.ModManager.xml
```

Current note: the mod manager stores values, but individual mods still need to read and apply those values.
