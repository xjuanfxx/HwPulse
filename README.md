# HwPulse

A lightweight hardware monitor for Windows, built on [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor).
It has a compact desktop window, a click-through in-game overlay and a local web dashboard you can open on a phone or tablet.

*Monitor de hardware ligero para Windows: ventana compacta, overlay para juegos y panel web local (con QR para el móvil). Interfaz en inglés y español.*

## Features

- CPU, GPU, memory, storage, network and battery: clocks, loads, temperatures, power, fans.
- Top processes by CPU, memory, GPU, disk and network.
- Every sensor, grouped by component (each group can be collapsed).
- Overlay: FPS, frametime and the values you pick, with custom colors, scale and position.
  Shortcuts: `Ctrl+Shift+F10` shows or hides it; `Ctrl+Shift+F11` moves it to the next corner.
- Web dashboard over SSE on `localhost`, optionally on your LAN with a QR code.
- CPU/GPU accent colors follow the vendor (Intel, AMD, NVIDIA).
- Goes to the tray when closed and uses about 0.3% CPU there.

## Requirements

- Windows 10/11 x64
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- Administrator rights. The sensor driver (PawnIO) and the ETW traces for FPS and per-process network need them.

## Build

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=true -o publish
```

Debug builds run without elevation (`app.dev.manifest`). For UI work, add `--no-elevate`.

## Credits

- Sensors: LibreHardwareMonitorLib (MPL-2.0)
- ETW: Microsoft.Diagnostics.Tracing.TraceEvent
- QR codes: QRCoder
- Font: Inter (SIL OFL 1.1)
