@echo off
rem GlassBoard lag diagnostic launcher.
rem Runs the read-only checker with execution policy bypassed and keeps the
rem window open so the report stays on screen (double-click this file).
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0CheckLag.ps1"
echo.
pause
