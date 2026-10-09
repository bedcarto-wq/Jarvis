@echo off
chcp 65001 >nul
echo Установка локальных моделей JARVIS (распознавание речи и OCR)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0download-models.ps1" %*
pause
