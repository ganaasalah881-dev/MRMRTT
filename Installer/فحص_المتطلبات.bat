@echo off
chcp 65001 >nul
REM =========================================================================
REM  MfgSystem — Pre-install requirements check
REM =========================================================================
setlocal EnableDelayedExpansion

echo.
echo ======================================================================
echo    MfgSystem — Requirements Check
echo ======================================================================
echo.

set "OK=1"

REM ── 1) OS ──
echo [1/6] Operating system
ver
ver | findstr /i "10.0 11.0" >nul
if errorlevel 1 (
    echo        [WARN] Windows 10/11 recommended.
) else (
    echo        [OK] Accepted
)
echo.

REM ── 2) Architecture ──
echo [2/6] Architecture
echo        PROCESSOR_ARCHITECTURE = %PROCESSOR_ARCHITECTURE%
if /i "%PROCESSOR_ARCHITECTURE%"=="AMD64" (
    echo        [OK] 64-bit — compatible with win-x64 package
) else (
    echo        [FAIL] Package is built for win-x64 only.
    set "OK=0"
)
echo.

REM ── 3) RAM ──
echo [3/6] Memory
for /f "skip=1 tokens=2 delims==" %%A in ('wmic ComputerSystem get TotalPhysicalMemory /value 2^>nul') do set "RAM=%%A"
if defined RAM (
    set /a RAMGB=!RAM:~0,-9!
    echo        Total RAM ≈ !RAMGB! GB
    if !RAMGB! LSS 4 echo        [WARN] 4 GB or more recommended.
)
echo.

REM ── 4) Disk space ──
echo [4/6] Disk space
for /f "tokens=3" %%A in ('dir "%SystemDrive%\" ^| findstr /i "bytes free"') do echo        Free on %SystemDrive%: %%A bytes
echo        (self-contained package ≈ 150-200 MB installed)
echo.

REM ── 5) .NET (informational — NOT required) ──
echo [5/6] .NET runtime (not required — package is self-contained)
where dotnet >nul 2>&1
if errorlevel 1 (
    echo        [OK] Not installed — and not needed.
) else (
    dotnet --version
)
echo.

REM ── 6) Local SQL Server instances (optional — app works in local SQLite mode) ──
echo [6/6] Local SQL Server instances (optional)
powershell -NoProfile -Command "try { $k=Get-Item 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL'; $v=$k.GetValueNames(); if ($v.Count -eq 0) { Write-Host '        None found - app will use local SQLite on first launch.' } else { foreach ($n in $v) { $d = '.'; if ($n -ne 'MSSQLSERVER') { $d = '.\' + $n }; Write-Host ('        Found: ' + $d) } } } catch { Write-Host '        Could not read registry - app will auto-detect at first launch.' }"
echo.

echo ======================================================================
if "%OK%"=="1" (
    echo    Machine ready — run "2-تنصيب.bat"
) else (
    echo    Fix the issues above before installing.
)
echo ======================================================================
echo.
pause
endlocal
