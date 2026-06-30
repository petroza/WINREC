@echo off
title WINREC - Install

echo.
echo  WINREC - Installation
echo  =====================
echo.

python --version >nul 2>&1
if errorlevel 1 (
    echo  [ERROR] Python not found.
    echo  Download Python 3.10+ from https://www.python.org/downloads/
    echo  During install, check "Add Python to PATH"
    pause
    exit /b 1
)

for /f "tokens=*" %%v in ('python --version 2^>^&1') do set PYVER=%%v
echo  Found: %PYVER%
echo.

echo  [1/2] Installing dependencies...
python -m pip install --user --quiet --upgrade pip
python -m pip install --user --quiet mss av numpy soundcard pywin32 pillow
if errorlevel 1 (
    echo  [ERROR] Dependency install failed.
    pause
    exit /b 1
)
echo        OK

echo  [2/2] Installing WINREC...
python -m pip install --user --quiet --force-reinstall .
if errorlevel 1 (
    echo  [ERROR] WINREC install failed.
    pause
    exit /b 1
)
echo        OK
echo.
echo  Done! Run with:  python -m winrec
echo.

set /p LAUNCH="  Launch WINREC now? [Y/N]: "
if /i "%LAUNCH%"=="Y" (
    echo.
    echo  Starting WINREC...
    python -m winrec
)

echo.
pause
