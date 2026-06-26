# WINREC — Python verze

Screen recorder pro Windows, instalovatelný přes `pip` bez admin práv.

## Instalace (bez admin práv)

```cmd
pip install --user mss av numpy soundcard pywin32 pillow
pip install --user git+https://github.com/petroza/WINREC.git#subdirectory=python
```

Nebo stáhni repo a instaluj lokálně:

```cmd
git clone https://github.com/petroza/WINREC.git
cd WINREC\python
pip install --user .
```

## Spuštění

```cmd
python -m winrec
```

nebo (po `pip install`):

```cmd
winrec
```

## Závislosti

| Balíček | Účel |
|---------|------|
| `mss` | Rychlé snímání obrazovky (DXGI) |
| `av` (PyAV) | H.264 / AAC enkódování, přibaluje FFmpeg |
| `numpy` | Zpracování snímků |
| `soundcard` | WASAPI loopback (zvuk systému) + mikrofon |
| `pywin32` | Win32 API — výčet oken, picker |
| `pillow` | Pomocné operace s obrázky |

Všechno instalovatelné přes `pip install --user` — **nevyžaduje admin práva**.

## Požadavky

- Windows 10 / 11
- Python 3.10+
- **Nevyžaduje** instalaci FFmpeg, .NET ani žádného jiného runtime
