@echo off
chcp 65001 >nul
setlocal EnableExtensions
set "APPVERSION=1.50.74"
set "APPDIR=%~dp0"
set "EXE=%APPDIR%MfgSystem.exe"
set "VERSION_FILE=%APPDIR%VERSION.txt"
if not exist "%EXE%" (
    echo [ERROR] MfgSystem.exe غير موجود بجانب المشغّل.
    pause
    exit /b 1
)
if not exist "%VERSION_FILE%" (
    echo [ERROR] VERSION.txt غير موجود؛ لا يمكن التأكد من هوية النسخة.
    pause
    exit /b 1
)
set "INSTALLED_VERSION="
for /f "usebackq delims=" %%V in ("%VERSION_FILE%") do if not defined INSTALLED_VERSION set "INSTALLED_VERSION=%%V"
if not "%INSTALLED_VERSION%"=="%APPVERSION%" (
    echo [ERROR] هذه الحزمة %INSTALLED_VERSION% وليست MfgSystem %APPVERSION%.
    pause
    exit /b 1
)
start "" /D "%APPDIR%" "%EXE%"
endlocal
exit /b 0
