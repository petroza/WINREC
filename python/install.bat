@echo off
setlocal enabledelayedexpansion
title WINREC - Installer

set "LOGFILE=%TEMP%\winrec-install-log.txt"
echo WINREC install log — %DATE% %TIME% > "%LOGFILE%"

echo.
echo  ===========================================
echo   WINREC - Screen Recorder  ^|  Installer
echo  ===========================================
echo.
echo  (Full log: %LOGFILE%)
echo.

:: ── 1. Find Python ─────────────────────────────────────────────────────────

set "PYTHON="

python --version >>"%LOGFILE%" 2>&1
if not errorlevel 1 ( set "PYTHON=python" & goto :python_found )

py --version >>"%LOGFILE%" 2>&1
if not errorlevel 1 ( set "PYTHON=py" & goto :python_found )

for %%P in (
    "%LOCALAPPDATA%\Programs\Python\Python314\python.exe"
    "%LOCALAPPDATA%\Programs\Python\Python313\python.exe"
    "%LOCALAPPDATA%\Programs\Python\Python312\python.exe"
    "%LOCALAPPDATA%\Programs\Python\Python311\python.exe"
    "%LOCALAPPDATA%\Programs\Python\Python310\python.exe"
    "%APPDATA%\Programs\Python\Python314\python.exe"
    "%APPDATA%\Programs\Python\Python313\python.exe"
    "%APPDATA%\Programs\Python\Python312\python.exe"
    "%APPDATA%\Programs\Python\Python311\python.exe"
    "%APPDATA%\Programs\Python\Python310\python.exe"
    "C:\Python314\python.exe"
    "C:\Python313\python.exe"
    "C:\Python312\python.exe"
    "C:\Python311\python.exe"
    "C:\Python310\python.exe"
    "C:\Program Files\Python314\python.exe"
    "C:\Program Files\Python313\python.exe"
    "C:\Program Files\Python312\python.exe"
    "C:\Program Files\Python311\python.exe"
    "C:\Program Files\Python310\python.exe"
    "C:\Program Files (x86)\Python314\python.exe"
    "C:\Program Files (x86)\Python313\python.exe"
    "C:\Program Files (x86)\Python312\python.exe"
    "C:\Program Files (x86)\Python311\python.exe"
    "C:\Program Files (x86)\Python310\python.exe"
) do (
    if exist %%P (
        set "PYTHON=%%~P"
        goto :python_found
    )
)

echo  [ERROR] Python 3.10+ not found on this computer. >>"%LOGFILE%"
echo  [ERROR] Python 3.10+ not found on this computer.
echo.
echo  Download from: https://www.python.org/downloads/
echo  During install, check "Add Python to PATH"
echo.
pause
exit /b 1

:python_found
echo  Python found: %PYTHON% >>"%LOGFILE%"

:: ── 2. Verify Python version (>= 3.10) ─────────────────────────────────────

"%PYTHON%" -c "import sys; sys.exit(0 if sys.version_info >= (3,10) else 1)" >>"%LOGFILE%" 2>&1
if errorlevel 1 (
    for /f "tokens=*" %%v in ('"%PYTHON%" --version 2^>^&1') do set PYVER=%%v
    echo  [ERROR] %PYVER% is too old. WINREC needs Python 3.10 or newer.
    echo  Download a newer version from: https://www.python.org/downloads/
    pause
    exit /b 1
)

for /f "tokens=*" %%v in ('"%PYTHON%" --version 2^>^&1') do set PYVER=%%v
echo  Python : %PYVER%
echo  Path   : %PYTHON%
echo.

:: ── 3. Choose install directory ────────────────────────────────────────────

set "DEFAULT_DIR=%LOCALAPPDATA%\WINREC"

echo  Where should WINREC be installed?
echo  (press Enter for default)
echo.
set "INSTALLDIR="
set /p INSTALLDIR="  Install path [%DEFAULT_DIR%]: "
if "!INSTALLDIR!"=="" set "INSTALLDIR=%DEFAULT_DIR%"
set "INSTALLDIR=!INSTALLDIR:"=!"

echo.
echo  Installing to: !INSTALLDIR!
echo  Installing to: !INSTALLDIR! >>"%LOGFILE%"
echo.

:: ── 4. Verify we can write to the target directory ─────────────────────────

if exist "!INSTALLDIR!\lib" (
    echo  Existing installation found — updating...
    echo.
)

mkdir "!INSTALLDIR!\lib" >>"%LOGFILE%" 2>&1

if not exist "!INSTALLDIR!\lib" (
    echo  [ERROR] Cannot create directory: !INSTALLDIR!
    echo  Possible causes:
    echo    - No write permission to that location
    echo    - Path is on a network/mapped drive that is not connected
    echo    - Path contains characters the filesystem rejects
    echo  Try a different path, e.g. your Desktop or Documents folder.
    pause
    exit /b 1
)

:: Verify write access with a real test file (mkdir can succeed on read-only
:: network shares in some edge cases; a file write is a stronger check).
echo test > "!INSTALLDIR!\.writetest" 2>>"%LOGFILE%"
if not exist "!INSTALLDIR!\.writetest" (
    echo  [ERROR] Directory created but is not writable: !INSTALLDIR!
    echo  Choose a local folder you have write access to.
    pause
    exit /b 1
)
del "!INSTALLDIR!\.writetest" >nul 2>&1

:: ── 5. Install Python dependencies one-by-one (so one failure doesn't hide
::      the others, and each gets a retry) ───────────────────────────────────

echo  [1/3] Installing dependencies...
echo.

"%PYTHON%" -m pip install --quiet --no-warn-script-location --upgrade pip >>"%LOGFILE%" 2>&1

set "PKGS=mss av numpy soundcard pywin32 pillow"
set "FAILED="

for %%K in (%PKGS%) do (
    echo        - %%K
    "%PYTHON%" -m pip install --quiet --no-warn-script-location --target="!INSTALLDIR!\lib" %%K >>"%LOGFILE%" 2>&1
    if errorlevel 1 (
        echo          retrying...
        "%PYTHON%" -m pip install --quiet --no-warn-script-location --target="!INSTALLDIR!\lib" %%K >>"%LOGFILE%" 2>&1
    )
    if errorlevel 1 (
        echo          [FAILED] %%K
        set "FAILED=!FAILED! %%K"
    )
)

if defined FAILED (
    echo.
    echo  [ERROR] Could not install: !FAILED!
    echo  Full details in: %LOGFILE%
    echo.
    echo  Common causes:
    echo    - No internet connection / proxy blocking pypi.org
    echo    - Corporate firewall blocking pip
    echo  If you are behind a proxy, run this first:
    echo    set HTTPS_PROXY=http://your-proxy:port
    pause
    exit /b 1
)

echo        All dependencies installed OK.

:: ── 6. Copy winrec package ─────────────────────────────────────────────────

echo.
echo  [2/3] Copying WINREC application files...
set "SRCDIR=%~dp0winrec"
if not exist "%SRCDIR%" (
    echo  [ERROR] winrec\ folder not found next to install.bat
    echo  Make sure you extracted the WHOLE zip, not just this file.
    pause
    exit /b 1
)

if exist "!INSTALLDIR!\lib\winrec" (
    rmdir /s /q "!INSTALLDIR!\lib\winrec" >>"%LOGFILE%" 2>&1
)
xcopy /e /i /q "%SRCDIR%" "!INSTALLDIR!\lib\winrec" >>"%LOGFILE%" 2>&1
if not exist "!INSTALLDIR!\lib\winrec\__main__.py" (
    echo  [ERROR] Failed to copy winrec package. See log: %LOGFILE%
    pause
    exit /b 1
)
echo        OK

:: ── 7. Verify everything actually imports before declaring success ─────────

echo.
echo  [3/3] Verifying installation...
set "PYTHONPATH=!INSTALLDIR!\lib"
"%PYTHON%" -c "import mss, av, numpy, soundcard, win32gui, PIL, winrec" >>"%LOGFILE%" 2>&1
if errorlevel 1 (
    echo  [ERROR] Installed but a module fails to import. See log: %LOGFILE%
    echo.
    echo  Last lines of log:
    powershell -NoProfile -Command "Get-Content -Tail 15 '%LOGFILE%'" 2>nul
    pause
    exit /b 1
)
echo        OK — all modules import correctly.

:: ── 8. Create launcher scripts ──────────────────────────────────────────────

(
echo @echo off
echo set PYTHONPATH=!INSTALLDIR!\lib
echo start "" "%PYTHON%" -m winrec
) > "!INSTALLDIR!\WINREC.bat"

(
echo @echo off
echo set PYTHONPATH=!INSTALLDIR!\lib
echo "%PYTHON%" -m winrec
echo pause
) > "!INSTALLDIR!\WINREC-debug.bat"

:: ── 9. Desktop shortcut ────────────────────────────────────────────────────

echo.
set "SHORTCUT="
set /p SHORTCUT="  Create Desktop shortcut? [Y/N]: "
if /i "!SHORTCUT!"=="Y" (
    powershell -NoProfile -Command ^
        "$s=(New-Object -COM WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop')+'\WINREC.lnk');$s.TargetPath='!INSTALLDIR!\WINREC.bat';$s.WorkingDirectory='!INSTALLDIR!';$s.Description='WINREC Screen Recorder';$s.Save()" >>"%LOGFILE%" 2>&1
    if not errorlevel 1 (
        echo  Desktop shortcut created.
    ) else (
        echo  Shortcut creation failed (not critical) — see log.
    )
)

:: ── 10. Done ───────────────────────────────────────────────────────────────

echo.
echo  ===========================================
echo   Installation complete!
echo  ===========================================
echo.
echo  Location : !INSTALLDIR!
echo  Launcher : !INSTALLDIR!\WINREC.bat
echo  Log      : %LOGFILE%
echo.

set "LAUNCH="
set /p LAUNCH="  Launch WINREC now? [Y/N]: "
if /i "!LAUNCH!"=="Y" (
    echo.
    start "" "!INSTALLDIR!\WINREC.bat"
)

echo.
pause
endlocal
