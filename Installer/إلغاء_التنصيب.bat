@echo off
chcp 65001 >nul
setlocal EnableExtensions EnableDelayedExpansion
set "APPNAME=MfgSystem"
set "APPVERSION=1.50.74"
set "EXE=%APPNAME%.exe"
set "SHORTCUTNAME=%APPNAME% %APPVERSION%"
set "DATADIR=%LocalAppData%\%APPNAME%\%APPVERSION%"
set "DEST="
set "NO_PAUSE=0"
set "KEEP_DATA=0"
set "DELETE_DATA=0"
for %%A in (%*) do (
    if /i "%%~A"=="/NO-PAUSE" set "NO_PAUSE=1"
    if /i "%%~A"=="/KEEP-DATA" set "KEEP_DATA=1"
    if /i "%%~A"=="/DELETE-DATA" set "DELETE_DATA=1"
)
if defined MFGSYSTEM_INSTALL_DIR if exist "%MFGSYSTEM_INSTALL_DIR%\%EXE%" set "DEST=%MFGSYSTEM_INSTALL_DIR%"
if not defined DEST if exist "%ProgramFiles%\%APPNAME%\%APPVERSION%\%EXE%" set "DEST=%ProgramFiles%\%APPNAME%\%APPVERSION%"
if not defined DEST if exist "%LocalAppData%\Programs\%APPNAME%\%APPVERSION%\%EXE%" set "DEST=%LocalAppData%\Programs\%APPNAME%\%APPVERSION%"

echo.
echo ======================================================================
echo    إزالة MfgSystem %APPVERSION%
echo ======================================================================
if not defined DEST (
    echo لم يتم العثور على تثبيت الإصدار %APPVERSION%.
    echo لم يتم حذف أي مجلد تثبيت أو بيانات.
    goto :Done
)
echo مجلد البرنامج: %DEST%
echo بيانات هذا الإصدار: %DATADIR% (تبقى افتراضياً)
echo.

set "MFGSYSTEM_TARGET_EXE=%DEST%\%EXE%"
powershell -NoProfile -Command "$target=[IO.Path]::GetFullPath($env:MFGSYSTEM_TARGET_EXE); Get-Process -Name '%APPNAME%' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ([IO.Path]::GetFullPath($_.Path) -ieq $target) } | Stop-Process -Force" >nul 2>&1
rmdir /s /q "%DEST%" >nul 2>&1
if exist "%DEST%" (
    echo [WARN] تعذرت إزالة بعض ملفات البرنامج؛ شغّل الإزالة بصلاحيات مناسبة بعد إغلاق النسخة.
) else (
    echo أزيلت ملفات البرنامج الخاصة بالإصدار %APPVERSION% فقط.
)
powershell -NoProfile -ExecutionPolicy Bypass -Command "$n='%SHORTCUTNAME%'; $d=[Environment]::GetFolderPath('Desktop'); $p=[Environment]::GetFolderPath('Programs'); Remove-Item -LiteralPath (Join-Path $d ($n+'.lnk')) -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath (Join-Path $p ($n+'.lnk')) -Force -ErrorAction SilentlyContinue" >nul 2>&1
echo.
if "%DELETE_DATA%"=="1" goto :DeleteData
if "%KEEP_DATA%"=="1" goto :KeepData
choice /c KD /t 15 /d K /m "الاحتفاظ ببيانات الإصدار وقاعدته المحلية؟ (K=احتفاظ، D=حذف بيانات 1.50.74 فقط؛ الافتراضي احتفاظ بعد 15 ثانية)"
if errorlevel 2 goto :DeleteData
goto :KeepData

:DeleteData
if exist "%DATADIR%" rmdir /s /q "%DATADIR%"
if exist "%DATADIR%" (
    echo [WARN] تعذر حذف مجلد البيانات: %DATADIR%
) else (
    echo حذفت بيانات الإصدار 1.50.74 فقط: %DATADIR%
)
goto :DataDone

:KeepData
echo احتُفظ ببيانات الإصدار: %DATADIR%
:DataDone
echo.
echo انتهت إزالة MfgSystem %APPVERSION%. لم تُمسّ بيانات أو تثبيتات إصدارات أخرى.
echo ======================================================================
:Done
if "%NO_PAUSE%"=="1" goto :DoneExit
pause
:DoneExit
endlocal
exit /b 0
