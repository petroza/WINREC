@echo off
setlocal enabledelayedexpansion
title WINREC - Installer

echo.
echo  ===========================================
echo   WINREC - Screen Recorder  ^|  Installer
echo  ===========================================
echo.

:: ── 1. Find Python ─────────────────────────────────────────────────────────

set PYTHON=

python --version >nul 2>&1
if not errorlevel 1 ( set PYTHON=python & goto :python_ok )

py --version >nul 2>&1
if not errorlevel 1 ( set PYTHON=py & goto :python_ok )

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
    if exist %%P ( set PYTHON=%%~P & goto :python_ok )
)

echo  [ERROR] Python 3.10+ not found on this computer.
echo.
echo  Download from: https://www.python.org/downloads/
echo  During install, check "Add Python to PATH"
echo.
pause
exit /b 1

:python_ok
for /f "tokens=*" %%v in ('"%PYTHON%" --version 2^>^&1') do set PYVER=%%v
echo  Python : %PYVER%
echo  Path   : %PYTHON%
echo.

:: ── 2. Choose install directory ────────────────────────────────────────────

set DEFAULT_DIR=%LOCALAPPDATA%\WINREC

echo  Where should WINREC be installed?
echo  (press Enter for default)
echo.
set /p INSTALLDIR="  Install path [%DEFAULT_DIR%]: "
if "!INSTALLDIR!"=="" set INSTALLDIR=%DEFAULT_DIR%

:: Remove surrounding quotes if user typed them
set INSTALLDIR=!INSTALLDIR:"=!

echo.
echo  Installing to: !INSTALLDIR!
echo.

:: ── 3. Create directories ──────────────────────────────────────────────────

if exist "!INSTALLDIR!\lib" (
    echo  Existing installation found — updating...
    echo.
)

mkdir "!INSTALLDIR!\lib" >nul 2>&1
if not exist "!INSTALLDIR!\lib" (
    echo  [ERROR] Cannot create directory: !INSTALLDIR!
    echo  Check that you have write permission to that path.
    pause
    exit /b 1
)

:: ── 4. Install Python dependencies into lib\ ──────────────────────────────

echo  [1/3] Installing dependencies...
"%PYTHON%" -m pip install --quiet --no-warn-script-location ^
    --target="!INSTALLDIR!\lib" ^
    mss av numpy soundcard pywin32 pillow

if errorlevel 1 (
    echo  [ERROR] Dependency install failed.
    pause
    exit /b 1
)
echo        OK

:: ── 5. Copy winrec package ─────────────────────────────────────────────────

echo  [2/3] Copying WINREC...
set SRCDIR=%~dp0winrec
if not exist "%SRCDIR%" (
    echo  [ERROR] winrec\ folder not found next to install.bat
    pause
    exit /b 1
)

if exist "!INSTALLDIR!\lib\winrec" (
    rmdir /s /q "!INSTALLDIR!\lib\winrec"
)
xcopy /e /i /q "%SRCDIR%" "!INSTALLDIR!\lib\winrec" >nul
if errorlevel 1 (
    echo  [ERROR] Failed to copy winrec package.
    pause
    exit /b 1
)
echo        OK

:: ── 6. Create WINREC.bat launcher ─────────────────────────────────────────

echo  [3/3] Creating launcher...

(
echo @echo off
echo set PYTHONPATH=!INSTALLDIR!\lib
echo start "" "%PYTHON%" -m winrec
) > "!INSTALLDIR!\WINREC.bat"

:: Also write a console version for debugging
(
echo @echo off
echo set PYTHONPATH=!INSTALLDIR!\lib
echo "%PYTHON%" -m winrec
echo pause
) > "!INSTALLDIR!\WINREC-debug.bat"

echo        OK

:: ── 7. Desktop shortcut ────────────────────────────────────────────────────

echo.
set /p SHORTCUT="  Create Desktop shortcut? [Y/N]: "
if /i "!SHORTCUT!"=="Y" (
    powershell -NoProfile -Command ^
        "$s=(New-Object -COM WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop')+'\WINREC.lnk');$s.TargetPath='!INSTALLDIR!\WINREC.bat';$s.WorkingDirectory='!INSTALLDIR!';$s.Description='WINREC Screen Recorder';$s.Save()" >nul 2>&1
    if not errorlevel 1 (
        echo  Desktop shortcut created.
    ) else (
        echo  Shortcut creation failed ^(not critical^).
    )
)

:: ── 8. Done ────────────────────────────────────────────────────────────────

echo.
echo  ===========================================
echo   Installation complete!
echo  ===========================================
echo.
echo  Location : !INSTALLDIR!
echo  Launcher : !INSTALLDIR!\WINREC.bat
echo.

set /p LAUNCH="  Launch WINREC now? [Y/N]: "
if /i "!LAUNCH!"=="Y" (
    echo.
    start "" "!INSTALLDIR!\WINREC.bat"
)

echo.
pause
endlocal
