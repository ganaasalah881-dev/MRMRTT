@echo off
chcp 65001 >nul
REM =========================================================================
REM  MfgSystem 1.50.74 — one-click, version-isolated Windows installer
REM  Installs only this release under a versioned program folder and keeps its
REM  settings/SQLite database under %LocalAppData%\MfgSystem\1.50.74.
REM =========================================================================
setlocal EnableExtensions EnableDelayedExpansion
set "APPNAME=MfgSystem"
set "APPVERSION=1.50.74"
set "EXE=%APPNAME%.exe"
set "SHORTCUTNAME=%APPNAME% %APPVERSION%"
set "SRC=%~dp0"
set "DATADIR=%LocalAppData%\%APPNAME%\%APPVERSION%"
set "MFGSYSTEM_VERIFY_EXE=%~dp0%APPNAME%.exe"
set "NO_LAUNCH=0"
set "NO_PAUSE=0"
for %%A in (%*) do (
    if /i "%%~A"=="/NO-LAUNCH" set "NO_LAUNCH=1"
    if /i "%%~A"=="/NO-PAUSE" set "NO_PAUSE=1"
)

echo.
echo ======================================================================
echo    %APPNAME% %APPVERSION% — التثبيت المستقل
echo ======================================================================
echo    الحزمة : %SRC%
echo.

REM 1) Fail closed: the archive must be a complete, matching build.
if not exist "%SRC%%EXE%" (
    echo [ERROR] %EXE% غير موجود بجانب المثبّت. فك حزمة النشر الكاملة أولاً.
    goto :InstallError
)
if not exist "%SRC%VERSION.txt" (
    echo [ERROR] VERSION.txt غير موجود؛ لن يتم تثبيت حزمة غير معرّفة الإصدار.
    goto :InstallError
)
set "PACKAGE_VERSION="
for /f "usebackq delims=" %%V in ("%SRC%VERSION.txt") do if not defined PACKAGE_VERSION set "PACKAGE_VERSION=%%V"
if not "!PACKAGE_VERSION!"=="%APPVERSION%" (
    echo [ERROR] إصدار الحزمة !PACKAGE_VERSION! لا يطابق المثبّت %APPVERSION%.
    goto :InstallError
)
if not exist "%SRC%SHA256.txt" (
    echo [ERROR] SHA256.txt غير موجود؛ لا يمكن التحقق من سلامة الملف التنفيذي.
    goto :InstallError
)
set "EXPECT="
for /f "usebackq delims=" %%H in ("%SRC%SHA256.txt") do if not defined EXPECT set "EXPECT=%%H"
if not defined EXPECT (
    echo [ERROR] SHA256.txt فارغ.
    goto :InstallError
)
set "ACTUAL="
for /f %%H in ('powershell -NoProfile -Command "(Get-FileHash -LiteralPath $env:MFGSYSTEM_VERIFY_EXE -Algorithm SHA256).Hash" 2^>nul') do set "ACTUAL=%%H"
if not defined ACTUAL (
    echo [ERROR] تعذر حساب SHA256 للملف التنفيذي.
    goto :InstallError
)
if /i not "!EXPECT!"=="!ACTUAL!" (
    echo [ERROR] SHA256 غير مطابق؛ الحزمة تالفة أو معدّلة.
    echo المتوقع: !EXPECT!
    echo الفعلي  : !ACTUAL!
    goto :InstallError
)
echo [OK] الإصدار %APPVERSION% وسلامة SHA256 متحققان.
echo.

REM 2) Use a distinct destination. The optional override is for isolated install tests.
if defined MFGSYSTEM_INSTALL_DIR (
    set "DEST=%MFGSYSTEM_INSTALL_DIR%"
) else (
    set "ADMIN=0"
    net session >nul 2>&1 && set "ADMIN=1"
    if "!ADMIN!"=="1" (
        set "DEST=%ProgramFiles%\%APPNAME%\%APPVERSION%"
    ) else (
        set "DEST=%LocalAppData%\Programs\%APPNAME%\%APPVERSION%"
    )
)
if not defined DEST (
    echo [ERROR] تعذر تحديد مجلد التثبيت.
    goto :InstallError
)
set "MFGSYSTEM_PACKAGE_DIR=%SRC%"
set "MFGSYSTEM_DEST_DIR=!DEST!"
powershell -NoProfile -Command "$src=[IO.Path]::GetFullPath($env:MFGSYSTEM_PACKAGE_DIR).TrimEnd('\')+'\'; $dst=[IO.Path]::GetFullPath($env:MFGSYSTEM_DEST_DIR).TrimEnd('\')+'\'; if($src.StartsWith($dst,[StringComparison]::OrdinalIgnoreCase) -or $dst.StartsWith($src,[StringComparison]::OrdinalIgnoreCase)){exit 1}; exit 0" >nul 2>&1
if errorlevel 1 (
    echo [ERROR] يجب أن يكون مجلد التثبيت منفصلاً عن مجلد الحزمة المصدر.
    goto :InstallError
)
echo [1/4] مجلد البرنامج: !DEST!
echo [2/4] بيانات هذا الإصدار: %DATADIR%

REM Stop only this version if it is running; never kill an older MfgSystem process.
set "MFGSYSTEM_TARGET_EXE=!DEST!\%EXE%"
powershell -NoProfile -Command "$target=[IO.Path]::GetFullPath($env:MFGSYSTEM_TARGET_EXE); Get-Process -Name '%APPNAME%' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ([IO.Path]::GetFullPath($_.Path) -ieq $target) } | Stop-Process -Force" >nul 2>&1
if not exist "%DATADIR%\logs" mkdir "%DATADIR%\logs" >nul 2>&1
if not exist "%DATADIR%\logs" (
    echo [ERROR] تعذر إنشاء مجلد بيانات الإصدار. تحقق من صلاحيات المستخدم.
    goto :InstallError
)

REM 3) Replace only this version's application files. Data is outside DEST and kept.
if exist "!DEST!" rmdir /s /q "!DEST!" >nul 2>&1
if exist "!DEST!" (
    echo [ERROR] تعذر إزالة ملفات الإصدار القديم من مجلد !DEST!.
    echo أغلق نسخة %APPNAME% %APPVERSION% ثم أعد تشغيل المثبّت.
    goto :InstallError
)
mkdir "!DEST!" >nul 2>&1
if not exist "!DEST!" (
    echo [ERROR] تعذر إنشاء مجلد التثبيت !DEST!.
    goto :InstallError
)
xcopy /e /i /y /q "%SRC%*.*" "!DEST!\" >nul 2>&1
if not exist "!DEST!\%EXE%" (
    echo [ERROR] فشل نسخ ملفات البرنامج إلى !DEST!.
    goto :InstallError
)
if not exist "!DEST!\تشغيل_1.50.74.bat" (
    echo [ERROR] مشغّل الإصدار غير موجود بعد النسخ.
    goto :InstallError
)
set "MFGSYSTEM_VERIFY_EXE=!DEST!\%EXE%"
set "COPIED_SHA="
for /f %%H in ('powershell -NoProfile -Command "(Get-FileHash -LiteralPath $env:MFGSYSTEM_VERIFY_EXE -Algorithm SHA256).Hash" 2^>nul') do set "COPIED_SHA=%%H"
if /i not "!EXPECT!"=="!COPIED_SHA!" (
    echo [ERROR] SHA256 بعد النسخ غير مطابق؛ أوقِف التشغيل.
    goto :InstallError
)
if not exist "!DEST!\VERSION.txt" (
    echo [ERROR] ملف الإصدار غير موجود بعد النسخ.
    goto :InstallError
)
set "INSTALLED_VERSION="
for /f "usebackq delims=" %%V in ("!DEST!\VERSION.txt") do if not defined INSTALLED_VERSION set "INSTALLED_VERSION=%%V"
if not "!INSTALLED_VERSION!"=="%APPVERSION%" (
    echo [ERROR] النسخة المنسوخة !INSTALLED_VERSION! لا تطابق %APPVERSION%.
    goto :InstallError
)
echo [3/4] نُسخت النسخة الصحيحة إلى !DEST!.

REM 4) Version-specific shortcuts never overwrite an older release's launcher.
set "MFGSYSTEM_INSTALL_DIR=!DEST!"
set "MFGSYSTEM_SHORTCUT_NAME=%SHORTCUTNAME%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ws=New-Object -ComObject WScript.Shell; $dest=$env:MFGSYSTEM_INSTALL_DIR; $name=$env:MFGSYSTEM_SHORTCUT_NAME; $target=Join-Path $dest 'تشغيل_1.50.74.bat'; $icon=(Join-Path $dest '%EXE%')+',0'; $desktop=[Environment]::GetFolderPath('Desktop'); $programs=[Environment]::GetFolderPath('Programs'); foreach($folder in @($desktop,$programs)) { if($folder) { $lnk=$ws.CreateShortcut((Join-Path $folder ($name+'.lnk'))); $lnk.TargetPath=$target; $lnk.WorkingDirectory=$dest; $lnk.IconLocation=$icon; $lnk.Description='MfgSystem %APPVERSION%'; $lnk.Save() } }" >nul 2>&1
if errorlevel 1 echo [WARN] تعذر إنشاء اختصار تلقائياً؛ شغّل !DEST!\تشغيل_1.50.74.bat.
echo [4/4] اكتمل التثبيت. لا تُقرأ إعدادات أو قواعد إصدارات سابقة.
echo.
echo الاختصار: %SHORTCUTNAME%
echo البرنامج: !DEST!\%EXE%
echo البيانات: %DATADIR%
echo السجل   : %DATADIR%\logs

echo.
if "%NO_LAUNCH%"=="1" goto :InstallSuccess
choice /c YN /t 10 /d Y /m "تشغيل MfgSystem %APPVERSION% الآن؟ (Y/N — تشغيل تلقائي بعد 10 ثوانٍ)"
if errorlevel 2 goto :InstallSuccess
start "" /D "!DEST!" "!DEST!\%EXE%"
:InstallSuccess
if "%NO_PAUSE%"=="1" goto :InstallSuccessExit
echo.
pause
:InstallSuccessExit
endlocal
exit /b 0

:InstallError
echo.
echo [ERROR] لم يكتمل التثبيت؛ لم تُعدّل بيانات أي إصدار آخر.
if "%NO_PAUSE%"=="1" goto :InstallErrorExit
echo.
pause
:InstallErrorExit
endlocal
exit /b 1
