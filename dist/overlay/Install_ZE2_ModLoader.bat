@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ========================================
echo ZE2 ModLoader 2.4.1 Installer
echo ========================================

echo Checking game folder...
if not exist "steam_api.dll" (
  echo ERROR: steam_api.dll not found.
  echo Run this from your Zombie Estate 2 game folder.
  exit /b 1
)

if not exist "BepInEx.NET.Framework.Launcher.exe" (
  echo ERROR: Missing BepInEx.NET.Framework.Launcher.exe
  echo Make sure you copied every file and folder from the ZE2_ModLoader folder into the game folder.
  exit /b 1
)

if not exist "Zombie Estate 2.exe.config" (
  echo ERROR: Missing Zombie Estate 2.exe.config
  echo This config tells the .NET launcher where to find BepInEx\core.
  echo Copy the entire contents of the ZE2_ModLoader folder into the Zombie Estate 2 game folder, then run this installer again.
  exit /b 1
)

if not exist "BepInEx\core\BepInEx.Preloader.Core.dll" (
  echo ERROR: Missing BepInEx\core\BepInEx.Preloader.Core.dll
  echo The BepInEx folder was not copied correctly.
  echo Copy the entire contents of the ZE2_ModLoader folder into the Zombie Estate 2 game folder, then run this installer again.
  exit /b 1
)

if not exist "BepInEx\core\BepInEx.Core.dll" (
  echo ERROR: Missing BepInEx\core\BepInEx.Core.dll
  echo The BepInEx folder was not copied correctly.
  echo Copy the entire contents of the ZE2_ModLoader folder into the Zombie Estate 2 game folder, then run this installer again.
  exit /b 1
)

if not exist "BepInEx\core\0Harmony.dll" (
  echo ERROR: Missing BepInEx\core\0Harmony.dll
  echo The BepInEx folder was not copied correctly.
  echo Copy the entire contents of the ZE2_ModLoader folder into the Zombie Estate 2 game folder, then run this installer again.
  exit /b 1
)

if not exist "BepInEx\plugins\ZE2.ModLoader.dll" (
  echo ERROR: Missing BepInEx\plugins\ZE2.ModLoader.dll
  echo The modloader plugin was not copied correctly.
  echo Copy the entire contents of the ZE2_ModLoader folder into the Zombie Estate 2 game folder, then run this installer again.
  exit /b 1
)

if not exist "_installer\Zombie Estate 2.exe" (
  echo ERROR: Missing launcher shim at _installer\Zombie Estate 2.exe
  exit /b 1
)

if not exist "Zombie Estate 2.real.exe" (
  if not exist "Zombie Estate 2.exe" (
    echo ERROR: Zombie Estate 2.exe not found.
    exit /b 1
  )

  echo Backing up original game exe to Zombie Estate 2.real.exe ...
  copy /y "Zombie Estate 2.exe" "Zombie Estate 2.real.exe" >nul
  if errorlevel 1 (
    echo ERROR: Failed to back up original exe.
    exit /b 1
  )
) else (
  echo Existing Zombie Estate 2.real.exe found. Keeping existing backup.
)

echo Installing launcher shim as Zombie Estate 2.exe ...
copy /y "_installer\Zombie Estate 2.exe" "Zombie Estate 2.exe" >nul
if errorlevel 1 (
  echo ERROR: Failed to install launcher shim.
  exit /b 1
)

echo.
echo Install complete.
echo Launch the game using Zombie Estate 2.exe
exit /b 0
