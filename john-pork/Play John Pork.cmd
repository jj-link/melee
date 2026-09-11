@echo off
start "" "%~dp0..\..\dolphin-player\Dolphin-x64\Dolphin.exe" -u "%~dp0DolphinUser" -C Dolphin.DSP.Volume=75 -e "%~dp0playable\Melee - John Pork.iso"
