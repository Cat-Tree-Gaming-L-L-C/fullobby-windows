@echo off
rem Double-click this file to run the HLL: Vietnam connect check.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Check-HllvConnect.ps1" %*
rem The script waits for Enter itself; this only catches PowerShell failing to start it.
if errorlevel 1 pause
