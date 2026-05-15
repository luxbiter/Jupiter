@echo off
setlocal

set "JUPITER_EXE=%~dp0Jupiter.exe"
if not exist "%JUPITER_EXE%" (
  set "JUPITER_EXE=%~dp0dist\Jupiter\Jupiter.exe"
)

if not exist "%JUPITER_EXE%" (
  echo Jupiter.exe was not found next to this uninstaller.
  echo Run this file from the published Jupiter folder.
  exit /b 1
)

"%JUPITER_EXE%" --uninstall-context-menu
