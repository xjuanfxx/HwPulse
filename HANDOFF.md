# HwPulse: handoff notes

Status snapshot so another developer or AI agent can pick up the work. Last updated 2026-11-26.

## What it is
HwPulse is a Windows hardware monitor.
- **Stack:** WPF on .NET 10, plus a WinForms tray icon.
- **Sensors:** LibreHardwareMonitorLib 0.9.6, through the PawnIO driver.
- **Parts:** a compact window, a click-through game overlay, and a local web dashboard (SSE, with a QR code for phones).
- **Language:** English UI by default, Spanish as an option.
- **Repo:** public at https://github.com/xjuanfxx/HwPulse.
- **Owner:** the user communicates in Spanish.

## Layout
| Path | Contents |
|---|---|
| `App.xaml(.cs)` | Startup and elevation, poll loop (1 s), tray, hotkeys, web server wiring, updater wiring, dev render mode |
| `Views/` | `MainWindow` (all pages in one XAML), `OverlayWindow`, `ColorsWindow` (HSL picker), `CloseDialog` |
| `ViewModels/` | `MainViewModel` split into partials: `.Pages`, `.Temps`, `.OverlayItems`, `.Behavior` (close action, autostart, update state) |
| `Controls/` | `SparkChart`, `MultiChart`, `Readout`, `DarkMenu` |
| `Monitoring/` | `SensorService` (two-phase open), `FpsMonitor` (ETW: DXGI/D3D9 presents and Kernel-Network per process), `GpuProcessMonitor`, `ProcessMonitor`, `WebServer`, `DiskMap` |
| `Core/` | `AppSettings` (validated JSON in `%AppData%\HwPulse`), `Loc` (EN/ES table), `Fmt`, `Native`, `ErrorLog` (`%AppData%\HwPulse\errors.log`), `BrandColors`, `Autostart`, `Updater` |
| `.github/workflows/release.yml` | Builds and publishes a release when a `v*.*.*` tag is pushed |
| `tools/make-icon.ps1` | Regenerates `Assets/HwPulse.ico` (BMP entries up to 128 px, PNG at 256 px) |

## Build and test
```powershell
dotnet build -c Debug          # Debug build: asInvoker manifest, no UAC prompt
bin\Debug\net10.0-windows\HwPulse.exe --no-elevate --render <dir>
```
- **`--render <dir>`:** renders every page to PNG and writes `en-fit.txt`. Every page except Sensors must say `OK`, meaning it fits without scrolling. The visible area is about 631 px, with the non-admin banner showing.
- **Other dev flags:**
  - `--timing`
  - `--bench <file>`
  - `--dump <file>`
  - `--font "<name>"`
  - `--minimized`
  - `--updated`, used internally after a self-update
- **Release build:**
  ```powershell
  dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=true -o publish
  ```
  - Release builds require admin (`app.manifest`).
  - To measure performance without UAC, add `-p:ApplicationManifest=app.dev.manifest`.
- **Releasing:** bump `<Version>` in `HwPulse.csproj`, commit, then `git tag vX.Y.Z && git push origin vX.Y.Z`. Actions then publishes three files:
  - `HwPulse.exe`
  - `HwPulse.exe.sha256`, which the updater requires
  - `HwPulse-X.Y.Z-win-x64.zip`
- **Performance at 0.1.1, measured over 60 s after startup:**
  - Window open: about 0.64% CPU.
  - Tray with the overlay and web on: about 0.63%.
  - Tray with both off: about 0.34%.
  - Memory: about 106 to 150 MB private, stable over the run.

## Done in 0.1.1 (not released yet unless a v0.1.1 tag exists)
- **FPS no longer flickers to "—".** ETW delivers events in bursts, so the old per-second count sometimes saw no events.
  - FPS is now measured over a sliding 1 s window, keyed on event timestamps.
  - The last value is held for 2.5 s when no new events arrive.
  - The last game process stays tracked when focus moves away briefly.
  - Not yet verified in a real game (Fortnite was the reported case).
- **Window behavior:**
  - Minimize sends the window to the tray.
  - The X asks "tray or exit", with a "Remember my choice" option.
  - The same choice is in Settings ("When closing": Ask / Tray / Exit), stored as `AppSettings.CloseAction`.
- **Start with Windows** uses a scheduled task (`HwPulse`) that runs at sign-in with highest privileges, is allowed on battery, and passes `--minimized`.
  - The registry Run key can't be used because Windows silently skips apps that require admin.
  - Needs admin; the switch reverts if task creation fails.
  - Not tested end to end yet, because the dev build runs without admin.
- **Auto-update** (`Core/Updater.cs`):
  - Checks the GitHub API `releases/latest` 20 s after startup, then every 6 hours. Release builds only.
  - Shows a tray balloon once per new version.
  - The link under the version in Settings has three states: "Check for updates", "Up to date" or "Update to X".
  - Installing downloads `HwPulse.exe.new` and checks its SHA-256. It then renames the running exe to `.old`, moves the new file into place, relaunches with `--updated` (which waits for the single-instance mutex) and exits.
  - The `.old` file is deleted on the next start.
  - The folder (zip) build only opens the release page.
- **Icon:** the `.ico` now stores BMP images for sizes up to 128 px. With PNG-only entries, Explorer showed the generic icon.
- **Defender:** it flagged 0.1.0 as `Trojan:Win32/Bearfoos.B!ml`, a machine-learning heuristic false positive on new unsigned single-file exes.
  - Product, company and description metadata are now in the exe.
  - The zip (folder) build is offered as an alternative download.
  - The real fix is code signing, or submitting the file to Microsoft as a false positive at https://www.microsoft.com/wdsi/filesubmission (the owner has to do this).
- **Smaller changes:**
  - Sensors page groups start collapsed.
  - Settings is compacted: shortcuts moved into the overlay panel, the colors button sits next to the style buttons, and the QR is smaller.
  - The temperature picker hides NVMe Warning/Critical threshold entries, which are fixed limits rather than readings.

## Pending / ideas
- Push 0.1.1: commit, push, then tag `v0.1.1` to trigger the workflow. Check that the Actions run succeeds.
- Test in a game:
  - FPS stability.
  - "Start with Windows" in the elevated Release build.
  - A real self-update from 0.1.1 to 0.1.2.
- Code signing, which would remove Defender and SmartScreen warnings.
- Dev render mode logs harmless `RenderTargetBitmap pixelWidth 0` errors for the collapsed Sensors page.
- Vulkan and OpenGL games don't report FPS, because only DXGI and D3D9 present events are captured.
