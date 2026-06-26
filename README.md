# WINREC

Jednoduchá aplikace pro nahrávání plochy Windows do MP4 (H.264) se zvukem.

## Funkce

- Nahrávání celé obrazovky nebo konkrétního okna
- Zvuk systému (loopback) i mikrofon
- Výstup do MP4, kódování H.264 hardwarově (NVENC / QuickSync / DXVA)
- Výběr kvality (1000 / 2000 / 4000 kbps)
- Časomíra záznamu

## Požadavky

- Windows 10 / 11 (verze 1903+)
- .NET 8 SDK nebo Runtime
- Visual Studio 2022 nebo `dotnet build`

## Sestavení

```
dotnet build -c Release
dotnet run
```

## Technologie

- [ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib) – wrapper nad Windows Media Foundation a Windows Graphics Capture API
- WPF (.NET 8)
- H.264 / AAC, výstup MP4
