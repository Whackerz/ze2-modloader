@echo off
setlocal EnableExtensions
cd /d "%~dp0"

if not exist "Zombie Estate 2.real.exe" (
  echo ERROR: Zombie Estate 2.real.exe not found. Nothing to restore.
  exit /b 1
)

echo Restoring original game executable...
copy /y "Zombie Estate 2.real.exe" "Zombie Estate 2.exe" >nul
if errorlevel 1 (
  echo ERROR: Restore failed.
  exit /b 1
)

echo Restore complete.
exit /b 0
