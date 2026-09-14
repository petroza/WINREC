# WINREC 2.0

Nahrávání obrazovky pro Windows — stavěné na **videopodcast a předvádění vlastních aplikací** se střihem v Adobe Premiere.

## Funkce

- **Co nahrávat:** celý monitor, okno aplikace (ze seznamu nebo kliknutím), oblast tažením myší
- **Výchozí kvalita:** nativní rozlišení (4K → 3840 × 2160), **60 fps konstantní**, H.264 High ~50 Mbit/s u 4K, AAC 192 kbps / 48 kHz; volitelně H.265, 30 fps, zmenšení na 1440p/1080p/720p
- **Zvuk:** systém + mikrofon / jen mikrofon / jen systém / bez zvuku; výběr zařízení, hlasitost do 200 %, převod mikrofonu na mono,
  **mikrofon zároveň jako samostatný WAV** (čistá stopa pro střih), zvuk jen z nahrávané aplikace
- **Mikrofon se pozná sám po připojení** (USB); „Směšovač stereo“ se nepovažuje za mikrofon
- **Indikátor v rohu obrazovky** (v nahrávce není vidět): blikající REC, čas, velikost souboru, **průběžná kontrola zápisu**
  (obraz přichází, soubor roste, zvuk přichází, mikrofon má signál) a **měřiče MIC/SYS s hlášením přebuzení**
- **Průběh na ikoně v hlavním panelu:** červený pruh se plní každou minutu, blikající tečka, pauza, varování, popisek s časem
- **Efekty kliknutí s galerií:** Vlnka, Dvojitá vlna, Pulz, Zaměřovač, Jiskry, Záře, Kruh při držení, Klasická tečka;
  barvy pro levé/pravé tlačítko, velikost, délka, kruh kolem kurzoru, zkušební plocha
- Odpočet, pauza, ztlumení mikrofonu, globální zkratky **Ctrl+Shift+F9 / F10 / F11**
- Okno WINREC se v nahrávce neobjeví, počítač během nahrávání neusne, vysoká priorita GPU (když GPU vytěžují jiné aplikace)
- Kontrola místa na disku (upozornění i automatické bezpečné zastavení), kontrola Media Foundation na Windows N

## Plynulost — „živá plocha“

ScreenRecorderLib při nehybném obrazu každých 100 snímků vyprázdní enkodér a zahodí tím 0,2–1 s videa (zvuk běží dál,
obraz stojí). WINREC proto během nahrávání obrazovky/oblasti drží v rohu monitoru 2×2 px okno vyjmuté z nahrávání,
které každý snímek nepatrně změní průhlednost. Naměřeno z tabulky `stts` v MP4 (4K60, 15 s):

| Varianta | Zamrznutí | fps |
|---|---|---|
| Desktop Duplication bez živé plochy | 7 | 52,9 |
| Desktop Duplication s živou plochou | 0 | 60,00 |
| Windows Graphics Capture s živou plochou | 0 | 60,00 |

Bez živé plochy navíc WGC zamrzne na ~1 s při zavření jakéhokoliv okna; s ní 0 zamrznutí (3 zavření oken).
Nahrávání samotného okna živou plochu mít nemůže — pro nejplynulejší video nahrávejte obrazovku nebo oblast.

## Požadavky

- Windows 10 2004+ / Windows 11, x64
- Na Windows N/KN „Media Feature Pack“ (WINREC na chybějící Media Foundation upozorní)
- Hotová aplikace nepotřebuje nainstalovaný .NET ani VC++ Redistributable (vše je přibalené)

## Sestavení

```
dotnet publish -c Release -r win-x64 --self-contained -o WINREC
```

Vyžaduje .NET 8 SDK. VC++ runtime se přibalí ze `System32` sestavujícího počítače.

## Diagnostika

- Logy: `%LOCALAPPDATA%\WINREC\logs` (v aplikaci „Další nastavení → Otevřít složku s logy“)
- `WINREC.exe --selftest <složka> [A…H]` — nahraje testovací videa bez okna a zapíše `selftest.txt`
  (A monitor+zvuk+WAV, B oblast H.265, C okno, D mikrofon, E 30 s kontrola zápisu, F statické okno, G/H zavírání oken WGC/DD;
  `K0K1K2K3` porovnání živé plochy)
- `WINREC.exe --screenshot <soubor.png>` — vykreslí hlavní okno, indikátor a galerii do PNG

## Struktura

| Soubor | Obsah |
|---|---|
| `RecordingEngine.cs` | sestavení voleb ScreenRecorderLib, bitrate, start/pauza/stop, kontrola zápisu |
| `AudioHub.cs` | zvuková zařízení (NAudio), hlídání připojení, měřiče, WAV mikrofonu |
| `OverlayWindows.cs` | výběr oblasti/okna, rámeček oblasti, indikátor, živá plocha, odpočet |
| `ClickEffects.cs`, `ClickEffectGallery.cs` | kreslení efektů kliknutí, hook myši, galerie |
| `MainWindow.xaml(.cs)`, `App.xaml(.cs)` | UI, zkratky, ikona v hlavním panelu, kontrola prostředí |
| `Native.cs`, `AppSettings.cs`, `SelfTest.cs` | Win32, nastavení a log, diagnostika |
| `web/` | starší webová verze (nahrávání v prohlížeči) |

## Technologie

WPF (.NET 8), [ScreenRecorderLib 7](https://github.com/sskodje/ScreenRecorderLib) (Media Foundation, Desktop Duplication / Windows Graphics Capture, NVENC/QuickSync), [NAudio](https://github.com/naudio/NAudio).

© Petr Závorka 2026
