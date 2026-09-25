@echo off
:: Install / update the agent by double click. Arguments go to install.ps1:
::   install.cmd -InstallPath D:\hlca -Port 8118
::   install.cmd -Uninstall
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
