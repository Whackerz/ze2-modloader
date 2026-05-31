param(
  [Parameter(Mandatory=$true)]
  [string]$GameExe,
  [string]$OutDll
)

$ErrorActionPreference = "Stop"

$GameExe = (Resolve-Path $GameExe).Path
if ([string]::IsNullOrWhiteSpace($OutDll)) {
  $gameRoot = Split-Path -Parent $GameExe
  $OutDll = Join-Path $gameRoot "BepInEx\plugins\ZE2.LegacySpriteBridge\ZE2ModLoader.dll"
}

$outDir = Split-Path -Parent $OutDll
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
$xnaFramework = "C:\Windows\Microsoft.NET\assembly\GAC_32\Microsoft.Xna.Framework\v4.0_4.0.0.0__842cf8be1de50553\Microsoft.Xna.Framework.dll"
$xnaGraphics = "C:\Windows\Microsoft.NET\assembly\GAC_32\Microsoft.Xna.Framework.Graphics\v4.0_4.0.0.0__842cf8be1de50553\Microsoft.Xna.Framework.Graphics.dll"
$xnaGame = "C:\Windows\Microsoft.NET\assembly\GAC_32\Microsoft.Xna.Framework.Game\v4.0_4.0.0.0__842cf8be1de50553\Microsoft.Xna.Framework.Game.dll"

Write-Host "Using game assembly reference $GameExe"

& $csc `
  /nologo `
  /target:library `
  /platform:x86 `
  /optimize+ `
  /out:$OutDll `
  /reference:$GameExe `
  /reference:$xnaFramework `
  /reference:$xnaGraphics `
  /reference:$xnaGame `
  /reference:System.Xml.Linq.dll `
  (Join-Path $PSScriptRoot "ModBootstrap.cs") `
  (Join-Path $PSScriptRoot "Properties\AssemblyInfo.cs")

if ($LASTEXITCODE -ne 0) {
  throw "csc.exe failed with exit code $LASTEXITCODE"
}

Write-Host "Built $OutDll"
