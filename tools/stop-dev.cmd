@echo off
powershell.exe -ExecutionPolicy Bypass -File "%~dp0stop-dev.ps1" %*
