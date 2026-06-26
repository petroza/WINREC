@echo off
chcp 65001 >nul
title WINREC — Instalace

echo.
echo  ╔══════════════════════════════════════╗
echo  ║   WINREC — automatická instalace     ║
echo  ╚══════════════════════════════════════╝
echo.

:: Ověř Python
python --version >nul 2>&1
if errorlevel 1 (
    echo  [CHYBA] Python nenalezen.
    echo  Stáhni Python 3.10+ z https://www.python.org/downloads/
    echo  Při instalaci zaškrtni "Add Python to PATH"
    pause
    exit /b 1
)

for /f "tokens=*" %%v in ('python --version 2^>^&1') do set PYVER=%%v
echo  Nalezen: %PYVER%
echo.

:: Instalace závislostí
echo  [1/2] Instaluji závislosti...
python -m pip install --user --quiet --upgrade pip
python -m pip install --user --quiet mss av numpy soundcard pywin32 pillow
if errorlevel 1 (
    echo  [CHYBA] Instalace závislostí selhala.
    pause
    exit /b 1
)
echo        OK

:: Instalace winrec
echo  [2/2] Instaluji WINREC...
python -m pip install --user --quiet --force-reinstall "git+https://github.com/petroza/WINREC.git#subdirectory=python"
if errorlevel 1 (
    echo.
    echo  [CHYBA] Instalace z GitHubu selhala.
    echo  Zkus spustit znovu nebo zkontroluj připojení k internetu.
    pause
    exit /b 1
)
echo        OK
echo.
echo  ══════════════════════════════════════
echo   Instalace dokončena!
echo  ══════════════════════════════════════
echo.
echo  Spuštění:   python -m winrec
echo.

:: Nabídni okamžité spuštění
choice /c AN /n /m "  Spustit WINREC teď? [A]no / [N]e: "
if errorlevel 2 goto :konec
if errorlevel 1 (
    echo.
    echo  Spouštím WINREC...
    python -m winrec
)

:konec
echo.
pause
