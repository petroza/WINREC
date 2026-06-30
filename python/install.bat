@echo off
title WINREC - Install

echo.
echo  WINREC - Installation
echo  =====================
echo.

:: Try python in PATH first
python --version >nul 2>&1
if not errorlevel 1 goto :found

:: Search common install locations
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
        set PYTHON=%%P
        goto :found_path
    )
)

:: Also try py launcher
py --version >nul 2>&1
if not errorlevel 1 (
    set PYTHON=py
    goto :found
)

echo  [ERROR] Python not found.
echo  Download Python 3.10+ from https://www.python.org/downloads/
echo  During install, check "Add Python to PATH"
pause
exit /b 1

:found_path
echo  Found Python at: %PYTHON%
set PYTHON=%PYTHON:"=%
goto :install

:found
set PYTHON=python

:install
for /f "tokens=*" %%v in ('"%PYTHON%" --version 2^>^&1') do set PYVER=%%v
echo  Found: %PYVER%
echo.

echo  [1/2] Installing dependencies...
"%PYTHON%" -m pip install --user --quiet --no-warn-script-location --upgrade pip
"%PYTHON%" -m pip install --user --quiet --no-warn-script-location mss av numpy soundcard pywin32 pillow
if errorlevel 1 (
    echo  [ERROR] Dependency install failed.
    pause
    exit /b 1
)
echo        OK

echo  [2/2] Installing WINREC...
"%PYTHON%" -m pip install --user --quiet --no-warn-script-location --force-reinstall .
if errorlevel 1 (
    echo  [ERROR] WINREC install failed.
    pause
    exit /b 1
)
echo        OK
echo.
echo  Done! To run WINREC:  python -m winrec
echo.

set /p LAUNCH="  Launch WINREC now? [Y/N]: "
if /i "%LAUNCH%"=="Y" (
    echo.
    echo  Starting WINREC...
    "%PYTHON%" -m winrec
)

echo.
pause
