@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0BuildRelease.ps1" -Runtime win-x64
if errorlevel 1 (
  echo.
  echo BUILD FAILED. Review the messages above.
  pause
  exit /b 1
)
echo.
echo Build complete. See ..\Output
pause
