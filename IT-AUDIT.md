# WINREC — Dokumentace pro IT audit / žádost o whitelist

> Tento dokument slouží jako podklad pro schválení aplikace WINREC firemním
> IT oddělením (whitelist v AppLocker / WDAC / endpoint security).

---

## 1. Shrnutí

| Položka | Hodnota |
|---|---|
| **Název** | WINREC — Screen Recorder |
| **Účel** | Nahrávání obrazovky / okna do MP4 (H.264/H.265) se zvukem |
| **Autor** | petr.zavorka@nova.cz |
| **Zdrojový kód** | https://github.com/petroza/WINREC (veřejný, plně auditovatelný) |
| **Licence / typ** | Interní nástroj, otevřený zdrojový kód |
| **Platforma** | Windows 10 / 11 (x64) |
| **Runtime** | .NET 8 (self-contained — runtime přibalen, neinstaluje se nic) |
| **Vyžaduje admin** | **NE** — běží pod běžným uživatelským účtem |
| **Instalace do systému** | **NE** — portable, spustí se z libovolné složky |

---

## 2. Co aplikace dělá

1. Pomocí oficiálního **Windows Graphics Capture API** (Windows.Graphics.Capture)
   zachytává obraz vybraného monitoru nebo okna.
2. Volitelně zachytává systémový zvuk (**WASAPI loopback**) a/nebo mikrofon.
3. Enkóduje obraz do **H.264/H.265 (MP4)** přes **Windows Media Foundation**
   (hardwarové enkódování NVENC / QuickSync / AMF, pokud je k dispozici).
4. Ukládá výsledný MP4 soubor **na lokální disk** do složky zvolené uživatelem.

To je vše. Aplikace nemá žádnou další funkcionalitu.

---

## 3. Co aplikace NEDĚLÁ (ověřeno ve zdrojovém kódu)

| Chování | Stav | Ověření |
|---|---|---|
| Síťová komunikace (HTTP, sockets, DNS) | ❌ Žádná | Žádný `HttpClient`, `Socket`, `WebClient`, `Dns` v kódu |
| Odesílání dat kamkoli | ❌ Ne | Žádný upload — výstup jen na lokální disk |
| Zápis do registru | ❌ Ne | Žádný `Registry`, `RegistryKey` v kódu |
| Spouštění jiných procesů | ❌ Ne | Žádný `Process.Start` |
| Eskalace práv / UAC | ❌ Ne | Žádný `runas`, žádný manifest s `requireAdministrator` |
| Instalace služeb / ovladačů | ❌ Ne | — |
| Persistence (autostart, scheduled task) | ❌ Ne | — |
| Keylogging / čtení vstupu jiných aplikací | ❌ Ne | Pouze vlastní GUI |
| Šifrování / ransomware chování | ❌ Ne | — |
| Obfuskace kódu | ❌ Ne | Čitelný C#, kompilovatelný ze zdroje |

**Jediná perzistentní data**, která aplikace zapisuje:
`%APPDATA%\WINREC\lastdir.txt` — textový soubor s poslední použitou cestou pro ukládání videí.

---

## 4. Použitá Windows API (P/Invoke)

Všechna nativní volání směřují do `user32.dll` a jsou to **pouze čtecí dotazy**
na okna a monitory — žádné z nich nemění stav systému:

| Funkce | Účel |
|---|---|
| `EnumDisplayMonitors` | Výčet monitorů (multi-monitor podpora) |
| `GetMonitorInfo` | Rozměry a pozice monitoru |
| `GetWindowRect` | Rozměry vybraného okna |
| `MonitorFromWindow` | Na kterém monitoru okno leží |
| `WindowFromPoint` | Okno pod kurzorem (funkce "vybrat kliknutím") |
| `GetAncestor` | Kořenové okno (pro správný výběr) |

Zachytávání obrazu a zvuku zajišťuje knihovna **ScreenRecorderLib** (open source,
NuGet), která je tenkým obalem nad oficiálními Microsoft API
(Windows Graphics Capture + Media Foundation).

---

## 5. Závislosti

| Komponenta | Zdroj | Pozn. |
|---|---|---|
| .NET 8 runtime | Microsoft | Self-contained — přibaleno v ZIP |
| ScreenRecorderLib 5.3.0 | NuGet (open source) | Obal nad Windows Graphics Capture + Media Foundation |

Žádné jiné externí závislosti. Žádné stahování za běhu.

---

## 6. Kontrolní součty (SHA-256)

> Pozn.: hashe odpovídají konkrétnímu buildu níže. Po novém buildu se mění —
> IT by mělo hashovat skutečně doručený soubor a porovnat s hodnotou,
> kterou současně sdělí autor.

```
WINREC.exe   ccb5aaa77fc56303fd501161ef56ded05372c338b3221f3a05667f311aeae2c0
WINREC.dll   90c81d3d3c278d1753ddb4ed3a71eab6e6064cd99fc31d7dddf950b109c460b4
```

Ověření na cílovém PC (PowerShell):
```powershell
Get-FileHash .\WINREC.exe -Algorithm SHA256
```

---

## 7. Doporučený postup whitelistu

Podle nasazeného řešení:

**AppLocker**
- Pravidlo typu *Publisher* nelze použít (exe není podepsán) → použít
  *File hash* pravidlo pro `WINREC.exe` a `WINREC.dll`.
- Alternativně *Path* pravidlo pro instalační složku.

**Windows Defender Application Control (WDAC)**
- Přidat hash do allow-list policy.

**Microsoft Defender for Endpoint / CrowdStrike / SentinelOne**
- Přidat hash souboru do *allow list* / *exclusions*.
- Případně označit GitHub repozitář jako důvěryhodný zdroj.

**SmartScreen**
- Po whitelistu v AppLocker/AV obvykle není nutné řešit; případně lze
  povolit přes *Reputation-based protection* výjimku.

---

## 8. Možnost ověření zdrojového kódu

Celý zdrojový kód je veřejný na **https://github.com/petroza/WINREC**.
IT může:
- Kód přečíst a ověřit výše uvedená tvrzení.
- Sestavit `.exe` z vlastního prostředí (`dotnet publish -c Release -r win-x64`)
  a porovnat chování.

---

## 9. Kontakt

V případě dotazů: **petr.zavorka@nova.cz**
