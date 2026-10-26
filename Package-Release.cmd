@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Package-Release.ps1" %*
if errorlevel 1 (
  echo.
  echo Packaging failed. Review the message above.
  pause
  exit /b 1
)
endlocal
