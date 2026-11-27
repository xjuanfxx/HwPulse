# HwPulse

A lightweight, free hardware monitor for Windows, built on [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor).
It has a compact desktop window, a fully customizable in-game overlay and a local web dashboard you can open on a phone or tablet.
It never injects into games, so it is safe to use with anti-cheat software.

*Monitor de hardware ligero y gratuito para Windows: ventana compacta, overlay para juegos totalmente personalizable y panel web local para el teléfono. No se inyecta en los juegos, así que es seguro con anti-cheats. Interfaz en inglés y español.* [Ver en español ↓](#en-español)

**[Download the latest release](https://github.com/xjuanfxx/HwPulse/releases/latest)** · [Changelog](CHANGELOG.md)

## Features

### Monitoring
- **CPU, GPU, memory, storage, network and battery:** clocks, loads, temperatures, power, fans and per-core detail.
- Works with **classic Intel, hybrid Intel (P-cores / E-cores) and AMD Ryzen** CPUs, and NVIDIA, AMD and Intel GPUs.
- **Top processes** by CPU, memory, GPU, disk and network.
- **Every sensor** on its own page, grouped by component (groups can be collapsed).
- **Windows built-in fallbacks** need no driver and no admin: CPU clock from Windows counters, and GPU temperature, fan and power % for any brand. They are used when the sensor library has no value.

### In-game overlay
- **FPS and frametime** for DirectX, **Vulkan and OpenGL** games. They come from Windows' own events (the same source as PresentMon), with no injection.
- Two ready-made styles (panel, single line) and a **Custom** style with a **visual editor**:
  - **Values:** 14 of them (FPS, frametime, CPU/GPU usage, temperature, clock and power, VRAM, RAM, total power, time). Each has its own label, colors (or automatic temperature/FPS colors), size, bold, progress bar and **mini graph**.
  - **Editing:** drag to reorder, with live preview.
  - **Design:** one line, column or grid layout, plus font, background, border, corners, padding and separator.
  - **Presets and sharing:** presets, plus **export / import** of designs as `.json` files.
  - **History:** **undo / redo** and **cancel**.
- **Position:** six fixed positions, or **move it with the mouse** anywhere on the screen.
- **Only show while playing:** the overlay hides itself when no game is running.
- **Shortcuts:** `Ctrl+Shift+F10` shows or hides the overlay; `Ctrl+Shift+F11` moves it to the next position.

### Web dashboard
- Live dashboard over SSE on `localhost`, optionally on your **local network with a QR code** for your phone.
- Compact landscape layout for phones and tablets used as a second screen.
- **Keep screen on** button, so a phone or tablet doesn't go to sleep.

### App
- **Lightweight:** about 0.3–0.6 % CPU and 120–160 MB RAM.
- **Tray:** minimizes to the tray; the close button asks whether to keep it in the tray or exit.
- **Start with Windows** (optional; starts in the tray).
- **Updates itself** from GitHub Releases (checksum verified) and shows an update notice in the window.
- **Installs the PawnIO sensor driver automatically** if it's missing (official installer, checksum verified).
- English and Spanish interface. CPU/GPU accent colors follow the vendor (Intel, AMD, NVIDIA).

## Install

1. Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64).
2. Download `HwPulse.exe` from the [latest release](https://github.com/xjuanfxx/HwPulse/releases/latest) and run it.
   It asks for **administrator rights**: the sensor driver (PawnIO) and the FPS / per-process network capture need them.

`HwPulse-x.y.z-win-x64.zip` is the same app as a folder, in case your antivirus dislikes the single file.

> **Windows Defender** may flag a brand-new unsigned exe with a generic machine-learning detection (`…!ml`). It's a false positive; you can report it at <https://www.microsoft.com/wdsi/filesubmission>.

## Requirements

- Windows 10/11 x64
- .NET 10 Desktop Runtime
- Administrator rights (for full sensor data and FPS). Without admin it still shows usage, memory, GPU data and the CPU clock.

## Build

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=true -o publish
```

**Debug builds:**
- They run without elevation (`app.dev.manifest`).
- For UI work, add `--no-elevate`.
- `--render <dir>` renders every page to PNG without showing windows.

**Releases** are built by GitHub Actions:
1. Add the version's section to `CHANGELOG.md`; it becomes the release notes.
2. Bump `<Version>` in `HwPulse.csproj`.
3. Push a tag such as `v0.1.8`.

**Diagnostic flags** (useful when helping a user):

| Flag | What it writes |
|---|---|
| `--dump <file>` | Every sensor, interpreted values, PawnIO status, Windows fallbacks and the full LibreHardwareMonitor report |
| `--fps-probe <file>` | FPS events per process and source (DirectX / kernel) for 5 s |
| `--native-probe <file>` | What the Windows built-in fallbacks return |
| `--selftest-cpu <file>` | How CPU cores are recognized for several CPU families |

`HANDOFF.md` has the full technical notes for contributors.

## Layout

| Folder | Contents |
|---|---|
| `Views/` | Main window, overlay, overlay editor, color pickers, close dialog |
| `ViewModels/` | `MainViewModel` (split by area) |
| `Controls/` | Charts, readouts, dark tray menu, slider behavior |
| `Monitoring/` | Sensors, FPS/network ETW, processes, NVML, Windows fallbacks, web server |
| `Core/` | Settings, overlay layout model, localization, formatting, Win32 interop, updater, PawnIO installer, logging |
| `Assets/`, `Fonts/`, `Web/` | Icon, Inter font, web dashboard page |

## En español

**HwPulse** es un monitor de hardware ligero y gratuito para Windows.

### Qué hace
- **Monitoreo:** CPU, GPU, RAM, discos, red y batería, con relojes, uso, temperaturas, potencia, ventiladores y detalle por núcleo.
- **Compatibilidad:** Intel clásico, Intel híbrido (núcleos P/E) y AMD Ryzen.
- **Overlay para juegos:** FPS en DirectX, Vulkan y OpenGL, sin inyectarse en el juego. Tiene un **editor visual** para personalizarlo por completo (datos, colores, tamaños, mini gráficas, disposición, presets y exportar/importar), y lo puedes mover con el ratón.
- **Panel web:** se abre en el teléfono con un QR y tiene un botón para mantener la pantalla encendida.
- **Instalación cómoda:** se actualiza solo e instala el driver PawnIO si falta.
- **Bajo consumo:** alrededor de 0.3–0.6 % de CPU.

### Instalar
1. Instala el [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64).
2. Descarga `HwPulse.exe` de la [última versión](https://github.com/xjuanfxx/HwPulse/releases/latest) y ábrelo. Pide permisos de administrador para leer todos los sensores y los FPS.

Si Windows Defender lo marca (`…!ml`), es un falso positivo por ser un exe nuevo sin firma; usa el `.zip` o repórtalo a Microsoft.

## Credits

- Sensors: LibreHardwareMonitorLib (MPL-2.0) with the PawnIO driver
- ETW: Microsoft.Diagnostics.Tracing.TraceEvent
- QR codes: QRCoder
- Font: Inter (SIL OFL 1.1)
