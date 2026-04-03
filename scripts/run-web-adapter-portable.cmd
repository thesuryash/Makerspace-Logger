@echo off
setlocal
set EXE=%~dp0..\dist\LocalWebAdapter-win-x64\LocalWebAdapter.exe
if not exist "%EXE%" (
  echo Portable adapter exe not found.
  echo Build it first with: scripts\publish-web-adapter.ps1
  exit /b 1
)
start "" "%EXE%"
start "" "http://127.0.0.1:5057"
