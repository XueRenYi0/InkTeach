@echo off
title UI mock - stealing-the-show test
cd /d "%~dp0"

set "EXE=bin\Release\net8.0-windows\ui-mock.exe"

echo Building (first run, or after the code changed)...
dotnet build ui-mock.csproj -c Release --nologo -v q
if errorlevel 1 (
  echo.
  echo Build failed. Most likely the panel is already running.
  echo Close that window first ^(press Esc in it^), then run this again.
  echo.
  pause
  exit /b 1
)

if not exist "%EXE%" (
  echo.
  echo Build finished but ui-mock.exe is missing. Run this from the repo root instead:
  echo     dotnet run --project tools/ui-mock -c Release -- --demo
  echo.
  pause
  exit /b 1
)

"%EXE%" --demo %*
if errorlevel 1 pause
