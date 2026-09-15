@echo off
title UI mock - self test
cd /d "%~dp0"

set "EXE=bin\Release\net8.0-windows\ui-mock.exe"

echo Building...
dotnet build ui-mock.csproj -c Release --nologo -v q
if errorlevel 1 (
  echo.
  echo Build failed. Most likely the panel is already running.
  echo Close that window first ^(press Esc in it^), then run this again.
  echo.
  pause
  exit /b 1
)

"%EXE%" --uitest
echo.
echo Exit code: %errorlevel%   (0 = all checks passed)
pause
