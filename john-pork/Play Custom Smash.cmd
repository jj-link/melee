@echo off
start "" "%~dp0..\..\dolphin-player\Dolphin-x64\Dolphin.exe" -u "%~dp0output\custom-smash\DolphinUser" -C Dolphin.DSP.Volume=75 -e "%~dp0playable\Melee - Custom Smash.iso"
