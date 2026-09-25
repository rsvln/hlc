@echo off
:: Install / update HomeLabControl by double click. Arguments go to install.ps1:
::   install.cmd -InstallPath D:\hlc -Port 8080
::   install.cmd -AdminUser admin -AdminPassword secret123
::   install.cmd -Uninstall
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
