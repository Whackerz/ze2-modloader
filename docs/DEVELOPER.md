# Developer Notes

Last updated: 2026-05-31

## Source Layout

```text
src/ZE2.BepInExParityBridge
src/ZE2.BepInExCorePatcher
src/ZE2.LegacySpriteBridge
```

## Runtime Pieces

### ZE2.ModLoader.dll

This is the friend's WIP compiled BepInEx loader. The original source is not currently included.

Current binary patch:

- `Plugin.ApplyMaps` returns `0`
- `Plugin.ApplyRawFile` returns `0`

This prevents the core loader from copying XML mod files into `Data/*`.

Patch source:

```text
src/ZE2.BepInExCorePatcher
```

Backup naming used in the working copy:

```text
ZE2.ModLoader.dll.pre_direct_maps.bak
```

Do not ship backup DLLs in releases.

### ZE2.BepInExParityBridge.dll

Companion BepInEx plugin that adds runtime hooks while original source is unavailable.

Main responsibilities:

- Patch `Level` construction and loading.
- Add main menu `Mods` entry.
- Add mod manager screens.
- Route custom map loading through direct mod-folder paths.
- Route talent XP/store functions through the bridge.

### ZE2.LegacySpriteBridge/ZE2ModLoader.dll

Rebuilt from the archived loader source.

Main responsibilities:

- Discover XML mods across mod roots.
- Load characters/guns/bullets from mod folders.
- Merge custom sprites.
- Resolve custom map files directly from `Mods`.
- Apply custom map tilesheets/light textures.
- Apply custom `<MapName>_Shadow.png`.
- Provide talent helper methods.

## Build

### Parity Bridge

Run from repo root:

```powershell
powershell -ExecutionPolicy Bypass -File .\src\ZE2.BepInExParityBridge\build.ps1 -GameRoot ".\path\to\Zombie Estate 2"
```

The script expects a local game folder with:

```text
Zombie Estate 2.real.exe
BepInEx/core/*.dll
BepInEx/plugins/ZE2.LegacySpriteBridge/ZE2ModLoader.dll
```

### Legacy Bridge

The archived bridge source uses the local .NET Framework compiler:

```powershell
powershell -ExecutionPolicy Bypass -File .\src\ZE2.LegacySpriteBridge\build.ps1 -GameExe ".\path\to\Zombie Estate 2\Zombie Estate 2.real.exe"
```

The original script was written for the older workspace layout. If building from this repo, verify `GameRoot`/output paths before publishing.

### Core Patcher

```powershell
dotnet run --project .\src\ZE2.BepInExCorePatcher\ZE2.BepInExCorePatcher.csproj -- ".\path\to\BepInEx\plugins\ZE2.ModLoader.dll"
```

## Merge Plan When Original Source Arrives

1. Move parity bridge hooks into the main BepInEx loader source.
2. Replace reflection calls to `ZE2ModLoader.ModBootstrap` with direct service calls.
3. Move direct map loading, custom shadows, custom map assets, and talent helpers into native main-loader code.
4. Remove the legacy bridge dependency once sprite/map/talent behavior is native.
5. Remove the binary patcher after `ApplyRawFile`/`ApplyMaps` behavior is implemented in source.

## Do Not Commit

Avoid committing:

- `Zombie Estate 2.exe`
- `Zombie Estate 2.real.exe`
- `Content/`
- `Data/`
- `SaveData/`
- `Logs/`
- runtime logs
- generated `ZE2_SpriteBridge`
- generated `ZE2_MapBridge`
- `_ManagedFileBackups`
- `.bak` DLLs
