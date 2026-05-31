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
