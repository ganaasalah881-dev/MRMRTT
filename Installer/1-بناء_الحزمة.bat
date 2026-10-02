@echo off
chcp 65001 >nul
REM =========================================================================
REM  MfgSystem 1.50.74 — Windows x64 production package builder
REM  Requires Windows + .NET 8 SDK. Output uses a release-specific path and
REM  never reads a pre-existing publish directory.
REM =========================================================================
setlocal EnableExtensions
set "ROOT=%~dp0.."
set "VERSION=1.50.74"
set "ARTIFACTS=%ROOT%\artifacts"
set "OUT=%ARTIFACTS%\MfgSystem_1.50.74_win-x64"
set "ZIP=%ARTIFACTS%\MfgSystem_1.50.74_win-x64.zip"
set "PROJ=%ROOT%\src\DatesErp.Desktop\DatesErp.Desktop.csproj"
set "NO_PAUSE=0"
for %%A in (%*) do if /i "%%~A"=="/NO-PAUSE" set "NO_PAUSE=1"

echo.
echo ======================================================================
echo    MfgSystem %VERSION% — بناء حزمة Production مستقلة
echo ======================================================================
echo.

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET SDK غير موجود. ثبّت .NET 8 SDK ثم أعد المحاولة.
    set "FAIL_CODE=1"
    goto :Fail
)
dotnet --version
for /f "tokens=1 delims=." %%V in ('dotnet --version') do set "SDK_MAJOR=%%V"
if not "%SDK_MAJOR%"=="8" (
    echo [ERROR] يلزم .NET 8 SDK لهذه الحزمة؛ النسخة الحالية: %SDK_MAJOR%.
    set "FAIL_CODE=1"
    goto :Fail
)
if not exist "%PROJ%" (
    echo [ERROR] ملف المشروع غير موجود: %PROJ%
    set "FAIL_CODE=1"
    goto :Fail
)
where py >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Python 3 with the Windows py launcher is required for policy checks.
    set "FAIL_CODE=1"
    goto :Fail
)
echo [1/14] Python policy unit tests
py -3 -m unittest discover -s "%ROOT%\tools\ci" -p "test_*.py"
if errorlevel 1 (
    echo [ERROR] Python policy unit tests failed.
    set "FAIL_CODE=1"
    goto :Fail
)

echo [2/14] Structural and legacy-double/XAML-hex policy checks
py -3 "%ROOT%\tools\ci\structural_checks.py"
if errorlevel 1 (
    echo [ERROR] Structural policy checks failed.
    set "FAIL_CODE=1"
    goto :Fail
)

echo [3/14] XAML element names vs C# references
py -3 "%ROOT%\tools\ci\verify_xaml_names.py"
if errorlevel 1 (
    echo [ERROR] XAML name checks failed.
    set "FAIL_CODE=1"
    goto :Fail
)

if exist "%OUT%" rmdir /s /q "%OUT%"
if exist "%ZIP%" del /q "%ZIP%"
if not exist "%ARTIFACTS%" mkdir "%ARTIFACTS%"
if not exist "%OUT%" mkdir "%OUT%"

echo [4/14] dotnet restore DateERP.sln
dotnet restore "%ROOT%\DateERP.sln" --nologo
if errorlevel 1 (
    echo [ERROR] Restore failed.
    set "FAIL_CODE=1"
    goto :Fail
)

echo [5/14] dotnet build DateERP.sln -c Release
dotnet build "%ROOT%\DateERP.sln" -c Release --no-restore --nologo -warnaserror
if errorlevel 1 (
    echo [ERROR] Release build failed.
    set "FAIL_CODE=1"
    goto :Fail
)

echo [6/14] Run the complete xUnit test suite
dotnet test "%ROOT%\tests\DatesErp.Tests\DatesErp.Tests.csproj" -c Release --no-build --no-restore --nologo --logger "console;verbosity=normal"
if errorlevel 1 (
    echo [ERROR] xUnit tests failed; no package will be published.
    set "FAIL_CODE=1"
    goto :Fail
)

echo [7/14] Full-cycle operational service acceptance
dotnet run --project "%ROOT%\tools\AcceptanceRunner\AcceptanceRunner.csproj" -c Release --no-build
if errorlevel 1 (
    echo [ERROR] Full-cycle acceptance failed.
    set "FAIL_CODE=1"
    goto :Fail
)

echo [8/14] Planning and capacity acceptance
dotnet run --project "%ROOT%\tools\PlanningAcceptance\PlanningAcceptance.csproj" -c Release --no-build
if errorlevel 1 (
    echo [ERROR] Planning acceptance failed.
    set "FAIL_CODE=1"
    goto :Fail
)

echo [9/14] Unit and inventory rule audit
dotnet run --project "%ROOT%\audit\UnitRuleAudit\UnitRuleAudit.csproj" -c Release --no-build
if errorlevel 1 (
    echo [ERROR] Unit/inventory audit failed.
    set "FAIL_CODE=1"
    goto :Fail
)

echo [10/14] Actual Windows WPF smoke tests
for %%P in (ReceivingWpfSmoke PlanningCapacityWpfSmoke TodayOrdersWpfSmoke ActualDeliveryWpfSmoke) do (
    echo Running %%P...
    dotnet run --project "%ROOT%\tools\%%P\%%P.csproj" -c Release --no-build
    if errorlevel 1 (
        echo [ERROR] WPF smoke failed: %%P
        set "FAIL_CODE=1"
        goto :Fail
    )
)
set "PRINT_SMOKE=%TEMP%\MfgSystem_1.50.74_print-smoke"
if exist "%PRINT_SMOKE%" rmdir /s /q "%PRINT_SMOKE%"
dotnet run --project "%ROOT%\tools\PrintWpfSmoke\PrintWpfSmoke.csproj" -c Release --no-build -- "%PRINT_SMOKE%"
if errorlevel 1 (
    echo [ERROR] Print WPF smoke failed.
    set "FAIL_CODE=1"
    goto :Fail
)

echo [11/14] Restore the win-x64 runtime pack
dotnet restore "%PROJ%" -r win-x64 --nologo
if errorlevel 1 (
    echo [ERROR] win-x64 runtime restore failed.
    set "FAIL_CODE=1"
    goto :Fail
)

echo [12/14] Publish self-contained win-x64 files
dotnet publish "%PROJ%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false --no-restore -o "%OUT%"
if errorlevel 1 (
    echo [ERROR] Publish failed.
    set "FAIL_CODE=1"
    goto :Fail
)
if not exist "%OUT%\MfgSystem.exe" (
    echo [ERROR] Publish finished without MfgSystem.exe.
    set "FAIL_CODE=1"
    goto :Fail
)

echo [13/14] Add installer, version-specific launcher, and integrity metadata
copy /y "%~dp02-تنصيب.bat" "%OUT%\" >nul
copy /y "%~dp0تشغيل_1.50.74.bat" "%OUT%\" >nul
copy /y "%~dp0فحص_المتطلبات.bat" "%OUT%\" >nul
copy /y "%~dp0إلغاء_التنصيب.bat" "%OUT%\" >nul
copy /y "%~dp0README_التنصيب.md" "%OUT%\" >nul
for %%F in (2-تنصيب.bat تشغيل_1.50.74.bat فحص_المتطلبات.bat إلغاء_التنصيب.bat README_التنصيب.md) do if not exist "%OUT%\%%F" (
    echo [ERROR] Failed to include installer file: %%F
    set "FAIL_CODE=1"
    goto :Fail
)
set "MFGSYSTEM_PROJECT_FILE=%PROJ%"
set "MFGSYSTEM_OUTPUT_DIR=%OUT%"
powershell -NoProfile -Command "$ErrorActionPreference='Stop'; $p=$env:MFGSYSTEM_PROJECT_FILE; $out=$env:MFGSYSTEM_OUTPUT_DIR; $m=[regex]::Match((Get-Content -Raw -LiteralPath $p), '<Version>([^<]+)</Version>'); if(-not $m.Success -or $m.Groups[1].Value -ne '%VERSION%'){ exit 2 }; Set-Content -LiteralPath (Join-Path $out 'VERSION.txt') -Value '%VERSION%' -Encoding ascii"
if errorlevel 1 (
    echo [ERROR] Project version does not match %VERSION% or VERSION.txt generation failed.
    set "FAIL_CODE=1"
    goto :Fail
)
powershell -NoProfile -Command "$ErrorActionPreference='Stop'; $exe=Join-Path $env:MFGSYSTEM_OUTPUT_DIR 'MfgSystem.exe'; $h=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToUpperInvariant(); if($h.Length -ne 64){exit 2}; Set-Content -LiteralPath (Join-Path $env:MFGSYSTEM_OUTPUT_DIR 'SHA256.txt') -Value $h -Encoding ascii"
if errorlevel 1 (
    echo [ERROR] SHA256 generation failed.
    set "FAIL_CODE=1"
    goto :Fail
)
if not exist "%OUT%\VERSION.txt" (
    echo [ERROR] VERSION.txt missing.
    set "FAIL_CODE=1"
    goto :Fail
)
if not exist "%OUT%\SHA256.txt" (
    echo [ERROR] SHA256.txt missing.
    set "FAIL_CODE=1"
    goto :Fail
)

echo [14/14] Create the full package archive
set "MFGSYSTEM_PACKAGE_ZIP=%ZIP%"
powershell -NoProfile -Command "$ErrorActionPreference='Stop'; $source=Join-Path $env:MFGSYSTEM_OUTPUT_DIR '*'; Compress-Archive -Path $source -DestinationPath $env:MFGSYSTEM_PACKAGE_ZIP -Force"
if errorlevel 1 (
    echo [ERROR] Package archive creation failed.
    set "FAIL_CODE=1"
    goto :Fail
)
if not exist "%ZIP%" (
    echo [ERROR] Package archive was not created.
    set "FAIL_CODE=1"
    goto :Fail
)

echo.
echo ======================================================================
echo   BUILD, TESTS, WPF SMOKE, AND PUBLISH COMPLETED
echo   Folder: %OUT%
echo   Zip   : %ZIP%
echo   Copy the folder or extract the zip, then run 2-تنصيب.bat.
echo   The installer verifies the release version and executable hash.
echo ======================================================================
goto :Success

:Fail
echo.
echo ======================================================================
echo   BUILD/PACKAGE FAILED. No production-ready claim should be made.
echo ======================================================================
if "%NO_PAUSE%"=="1" goto :FailExit
echo.
pause
:FailExit
endlocal & exit /b %FAIL_CODE%

:Success
if "%NO_PAUSE%"=="1" goto :SuccessExit
echo.
pause
:SuccessExit
endlocal & exit /b 0
