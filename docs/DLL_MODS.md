# DLL Mod Authoring Guide

Last updated: 2026-05-31

DLL mods are for behavior changes that cannot be expressed with XML content. Use them for custom game modes, new menus, altered wave rules, progression systems, enemy behavior changes, UI patches, or other runtime logic.

## How DLL Mods Load

`ZE2.ModLoader.dll` scans:

```text
BepInEx/plugins/Mods/*.dll
```

For each DLL, it finds public or internal non-abstract classes that implement:

```csharp
namespace ZE2.ModLoader
{
    public interface IZe2Mod
    {
        string Name { get; }
        string Version { get; }
        void OnLoad();
    }
}
```

The loader creates the class and calls `OnLoad()` once during startup.

## Minimal DLL Mod

```csharp
using BepInEx.Logging;
using ZE2.ModLoader;

namespace MyCoolMode
{
    public sealed class MyCoolModeMod : IZe2Mod
    {
        public string Name => "My Cool Mode";
        public string Version => "1.0.0";

        private static ManualLogSource log;

        public void OnLoad()
        {
            log = Logger.CreateLogSource("My Cool Mode");
            log.LogInfo("My Cool Mode loaded.");
        }
    }
}
```

## Minimal Project File

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>disable</Nullable>
    <AssemblyName>MyCoolMode</AssemblyName>
    <RootNamespace>MyCoolMode</RootNamespace>
    <PlatformTarget>x86</PlatformTarget>
    <Prefer32Bit>true</Prefer32Bit>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="ZE2.ModLoader">
      <HintPath>..\..\dist\overlay\BepInEx\plugins\ZE2.ModLoader.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="BepInEx.Core">
      <HintPath>..\..\dist\overlay\BepInEx\core\BepInEx.Core.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="0Harmony">
      <HintPath>..\..\dist\overlay\BepInEx\core\0Harmony.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

If the mod lives inside this repository, prefer a `ProjectReference` to `src/ZE2.ModLoader/ZE2.ModLoader.csproj`, like the sample mods do.

## Patching Game Behavior

Use Harmony patches from `OnLoad()` for most behavior mods.

```csharp
using HarmonyLib;
using System;
using System.Reflection;
using ZE2.ModLoader;

namespace MyCoolMode
{
    public sealed class MyCoolModeMod : IZe2Mod
    {
        public string Name => "My Cool Mode";
        public string Version => "1.0.0";

        private static readonly Harmony Harmony = new Harmony("myname.ze2.mycoolmode");

        public void OnLoad()
        {
            Type waveSelectType = AccessTools.TypeByName("ZombieEstate2.XboxWaveSelect");
            MethodInfo setup = AccessTools.Method(waveSelectType, "Setup");
            MethodInfo postfix = AccessTools.Method(typeof(MyCoolModeMod), nameof(SetupPostfix));

            if (setup != null && postfix != null)
            {
                Harmony.Patch(setup, postfix: new HarmonyMethod(postfix));
            }
        }

        private static void SetupPostfix(object __instance)
        {
            // Add menu entries, change defaults, or inspect private fields here.
        }
    }
}
```

Use `AccessTools.TypeByName("ZombieEstate2.SomeType")` when possible. It avoids needing to reference every game assembly directly and makes missing methods easier to handle gracefully.

## Custom Game Modes

A custom game mode usually needs three pieces:

- a menu hook that lets the player select the mode
- state that remembers the selected mode
- patches that alter waves, difficulty, zombies, rewards, or win conditions while that mode is active

`src/ZE2.SampleMods/ZE2.EndlessPlusMod` is the best starting point. It demonstrates:

- adding `Unlimited+` to the mode select menu
- intercepting mode selection
- modifying global difficulty fields
- changing wave spawn pressure
- changing zombie speed
- resetting mode state when returning to main menu

## Progression Or Menu Mods

`src/ZE2.SampleMods/ZE2.ProgressionRevivalMod` demonstrates a larger DLL mod:

- tracking runtime XP
- awarding talent points
- adding UI behavior to the store
- drawing custom HUD/store text
- patching multiple game systems from one mod

Use this as a reference for bigger systems, but start from the smaller `EndlessPlusMod` pattern for new work.

## Mod Options

XML manifests can declare options for the in-game Mods menu. Values are stored in:

```text
BepInEx/config/ZE2.ModManager.xml
```

Current limitation: there is not yet a clean helper API for DLL mods to read those options. For now, a DLL mod can read that XML file manually. A future loader API should expose option lookup directly.

## Install A DLL Mod

Build the mod, then copy the compiled DLL to:

```text
BepInEx/plugins/Mods/MyCoolMode.dll
```

Restart the game. Check:

```text
BepInEx/LogOutput.log
```

Expected signs:

- `ZE2.ModLoader` starts
- your DLL appears as loaded
- your `OnLoad()` log messages appear

## Safety Guidelines

- Target `.NET Framework 4.8` and `x86`.
- Give each Harmony instance a unique ID, such as `author.ze2.modname`.
- Always null-check reflected types, fields, and methods.
- Keep patches small and log skipped patches clearly.
- Avoid writing into game `Data/*`; prefer runtime changes or mod folder assets.
- Avoid hardcoding personal absolute paths.
- Expect game internals to use private fields and compiler-generated method names.
