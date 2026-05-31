param(
  [Parameter(Mandatory=$true)]
  [string]$GameRoot,
  [string]$OutDll
)

$ErrorActionPreference = "Stop"

$gameRootFull = (Resolve-Path $GameRoot).Path
$pluginsRoot = Join-Path $gameRootFull "BepInEx\plugins"
if ([string]::IsNullOrWhiteSpace($OutDll)) {
  $OutDll = Join-Path $pluginsRoot "ZE2.BepInExParityBridge.dll"
}

$bridgeDll = Join-Path $pluginsRoot "ZE2.LegacySpriteBridge\ZE2ModLoader.dll"
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"

$gameExe = Join-Path $gameRootFull "Zombie Estate 2.real.exe"
if (-not (Test-Path $gameExe)) {
  $gameExe = Join-Path $gameRootFull "Zombie Estate 2.exe"
}

$xnaFramework = "C:\Windows\Microsoft.NET\assembly\GAC_32\Microsoft.Xna.Framework\v4.0_4.0.0.0__842cf8be1de50553\Microsoft.Xna.Framework.dll"
$xnaGraphics = "C:\Windows\Microsoft.NET\assembly\GAC_32\Microsoft.Xna.Framework.Graphics\v4.0_4.0.0.0__842cf8be1de50553\Microsoft.Xna.Framework.Graphics.dll"
$xnaGame = "C:\Windows\Microsoft.NET\assembly\GAC_32\Microsoft.Xna.Framework.Game\v4.0_4.0.0.0__842cf8be1de50553\Microsoft.Xna.Framework.Game.dll"
$bepCore = Join-Path $gameRootFull "BepInEx\core\BepInEx.Core.dll"
$bepNet = Join-Path $gameRootFull "BepInEx\core\BepInEx.NET.Common.dll"
$harmony = Join-Path $gameRootFull "BepInEx\core\0Harmony.dll"

& $csc `
  /nologo `
  /target:library `
  /platform:x86 `
  /optimize+ `
  /out:$OutDll `
  /reference:$gameExe `
  /reference:$xnaFramework `
  /reference:$xnaGraphics `
  /reference:$xnaGame `
  /reference:$bepCore `
  /reference:$bepNet `
  /reference:$harmony `
  /reference:$bridgeDll `
  /reference:System.Xml.Linq.dll `
  (Join-Path $PSScriptRoot "Plugin.cs") `
  (Join-Path $PSScriptRoot "ModManagerMenu.cs") `
  (Join-Path $PSScriptRoot "ModManagerState.cs") `
  (Join-Path $PSScriptRoot "Properties\AssemblyInfo.cs")

if ($LASTEXITCODE -ne 0) {
  throw "csc.exe failed with exit code $LASTEXITCODE"
}

Write-Host "Built $OutDll"
