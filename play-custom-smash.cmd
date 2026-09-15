@echo off
setlocal
powershell.exe -NoLogo -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0tools\play_custom_smash.ps1" %*
if errorlevel 1 (
    echo.
    pause
    exit /b 1
)
