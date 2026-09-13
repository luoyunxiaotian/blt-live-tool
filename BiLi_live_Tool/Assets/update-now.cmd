@echo off
chcp 65001 >nul
title 直播小帮手 · 更新引导
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0update-now.ps1"
echo.
pause
