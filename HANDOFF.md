# HwPulse: handoff notes

Status snapshot so another developer or AI agent can pick up the work. Last updated 2026-11-26 (evening), after the optimization/stability pass and the UI polish round.

## What it is
HwPulse is a Windows hardware monitor.
- **Stack:** WPF on .NET 10, plus a WinForms tray icon.
- **Sensors:** LibreHardwareMonitorLib 0.9.6, through the PawnIO driver (NOT WinRing0: LHM loads PawnIO bytecode modules via `DeviceIoControl` and expects the PawnIO service to be installed; `Computer.Close()` unloads the modules).
- **Parts:** a compact window, a click-through game overlay, and a local web dashboard (SSE, with a QR code for phones).
- **Language:** English UI by default, Spanish as an option.
- **Repo:** public at https://github.com/xjuanfxx/HwPulse.
- **Owner:** the user communicates in Spanish.

## Layout
| Path | Contents |
|---|---|
| `App.xaml(.cs)` | Startup and elevation, poll loop (1 s), tray, hotkeys, web server wiring, updater wiring, dev render mode |
| `Views/` | `MainWindow` (all pages in one XAML), `OverlayWindow`, `CloseDialog` (redesigned, see below), `ColorsWindow` (HSL picker) |
| `ViewModels/` | `MainViewModel` split into partials: `.Pages`, `.Temps`, `.OverlayItems`, `.Behavior` |
| `Controls/` | `SparkChart`, `MultiChart` (both consume `SeriesView`, see A5), `Readout`, `DarkMenu` (WinForms tray menu) |
| `Monitoring/` | `SensorService` (two-phase open + cached sensor map), `FpsMonitor` (ETW: DXGI/D3D9 presents; Kernel-Network per process, on demand), `GpuProcessMonitor`, `ProcessMonitor`, `WebServer`, `DiskMap` |
| `Core/` | `AppSettings`, `Loc`, `Fmt` (+ `RingBuffer`/`SeriesView`/`ChartSeries`), `Native` (+ `StyleMenuFrame`), `ErrorLog`, `BrandColors`, `Autostart`, `Updater` |
| `.github/workflows/release.yml` | Builds and publishes a release when a `v*.*.*` tag is pushed |

## Build and test
```powershell
dotnet build -c Debug          # Debug build: asInvoker manifest, no UAC prompt
bin\Debug\net10.0-windows\HwPulse.exe --no-elevate --render <dir>
```
- **`--render <dir>`:** renders every page to PNG and writes `en-fit.txt`/`es-fit.txt`. Every page except Sensors must say `OK` (no scrolling). Expect **32 files** and the process must **exit on its own**. If a PNG is missing, something measured 0 (see gotcha 5). Works even with the overlay hidden (`OverlayVisible: false` in `%AppData%\HwPulse\settings.json`).
- **Other dev flags:** `--timing`, `--bench <file>`, `--dump <file>`, `--font "<name>"`, `--minimized`, `--no-elevate`, `--updated` (used internally after a self-update).
- **Release build:**
  ```powershell
  dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=true -o publish
  ```
  - Release builds require admin (`app.manifest`).
  - To measure performance without UAC, add `-p:ApplicationManifest=app.dev.manifest`.
- **Releasing:** bump `<Version>` in `HwPulse.csproj`, commit, then `git tag vX.Y.Z && git push origin vX.Y.Z`. Actions publishes three files: `HwPulse.exe`, `HwPulse.exe.sha256`, `HwPulse-X.Y.Z-win-x64.zip`.
- **Performance at 0.1.1 (before the pass), measured over 60 s:** window open ~0.64% CPU; tray with overlay+web on ~0.63%; tray with both off ~0.34%; **106–150 MB private, stable**. Re-measure after the pass — expected gains: no TraceEvent/ETW at all when unused, far less per-second garbage, but ICU/WinForms are still loaded (see backlog A1).

---

## DONE in this pass (git: `4ff6db5` → `7356cb1`, on top of baseline `6cb461f`)

A full audit (architecture, leaks, RAM, threads) drove three rounds of changes. Every round was build-verified (0 errors / 0 warnings) and the result was double-reviewed by a second model (diff review + a runtime-focused audit).

### Round 1 — PawnIO/ETW stability
- **C1:** `FpsMonitor.TryStart` stops an orphaned "HwPulse-FPS" session first (`TraceEventSession.GetActiveSession(...)?.Stop()`), so a hard kill (crash/power loss) no longer leaves FPS dead until reboot.
- **C2/C3:** `App._pendingResources` — resources created on background threads (`SensorService`, `FpsMonitor`) register themselves at creation and unregister when assigned to their field; `ExitApp` AND `ProcessExit` dispose fields + everything pending. Closes the startup race where a quick exit left LHM's `Computer` open (PawnIO modules loaded) or an ETW session alive. `StartSensorsAsync` checks `IsExiting` before starting the poll loop; `_fps` is `volatile`.
- **C7:** `FpsMonitor.ResetNetwork()` purges per-process network data while the window is hidden in tray (it used to grow one entry per PID forever).

### Round 2 — structural RAM / UI unblocking
- **A3 (biggest structural win):** the FPS ETW session is **no longer created at startup**. `App.EnsureFpsMonitor()` (called each tick) creates it only when: overlay visible AND fps/frametime checked, OR the Network page is open, OR a web client is connected (`WebServer.HasClients`). Kernel-Network (per-process network) toggles on/off with the Network page (`FpsMonitor.SetNetworkEnabled`, EnableProvider/DisableProvider mid-session; the state flag is only set after success so a transient failure retries next tick).
- **A4:** web JSON is only built when someone is watching (`WebServer.NeedsJson` = SSE clients > 0, recent `/api/stats` poll, or the first payload not sent yet).
- **C4:** drive reads moved off the UI thread (`RefreshDrivesAsync` with a busy-guard), and `DriveType` is checked BEFORE `IsReady` (a dead mapped network drive could freeze the window for seconds).
- **A2 — attempted and REVERTED:** `<InvariantGlobalization>true</InvariantGlobalization>` broke WPF at runtime (see gotcha 1). ICU stays. The −3..−8 MB estimate for A2 is forfeited.

### Round 3 — CPU/allocations in the 1 s loop
- **B1:** `BarRow/ProcPair/ProcView/NetView/DriveView` are now `Observable` row classes with `Update()` (same property names as the old records; XAML templates are keyed, no DataType). The lists (`TempRows, FanRows, TopCpu, TopMem, TopCpuPage, TopGpu, TopDisk, TopNet, NetRows, Drives`) are fixed `ObservableCollection`s filled by position — WPF no longer rebuilds every ItemsControl each second. Pattern matches the pre-existing `CoreRow`.
- **B3:** `SensorService` keeps a cached map (flat hardware list, per-hardware `ISensor[]`, `(type, lowercase name) → sensor` dictionaries, `Identifier.ToString()` keys, cached "CPU Core #N" regex roles, reused dictionaries per tick). Invalidated ONLY by LHM events (`HardwareAdded/Removed`, per-hardware `SensorAdded/Removed` set `_mapDirty`), with a cheap `_pc.Hardware.Count` safety net. Kills the double `SelectMany(Flatten)`, ~15 `ToList()`, per-sensor regex and per-tick `Identifier.ToString()`.
- **A5:** `RingBuffer.CopyTo(double[])` + `SeriesView`/`ChartSeries` (two rotating buffers so WPF sees a reference change) — the 9 `*Series` VM properties no longer allocate ~43 KB/s. `SparkChart`/`MultiChart` DPs take `SeriesView` and copy into their own reused `_slice`/`_slot` buffers instead of `Skip().ToArray()` per render.

### Runtime bugs found by actually running the app (the build was clean for all of them)
1. **A2/WPF culture bug** (above): window invisible, process alive — diagnosed from `%AppData%\HwPulse\errors.log` ("Cannot find non-neutral culture related to 'en-us'"). Reverted.
2. **`--render` hang with the overlay hidden:** the overlay's detached content is never measured (`OverlayWindow.Render` early-returns when `!IsVisible`) → `RenderTargetBitmap(0)` → the exception is eaten by PollLoop's catch → `ExitApp` never runs. Fixed with `OverlayWindow.RenderNow()`/`RenderCore()` split + a guard inside `Save()` (measure anything with `ActualWidth < 1`), + `_overlay.RenderNow()` before the overlay captures.
3. **Sticky `SetNetworkEnabled` flag:** a transient `EnableProvider` failure left per-process network dead for the whole session; flag now set only after success.
4. **`RebuildMap` partial state on throw:** wrapped in try/catch that re-marks `_mapDirty` and rethrows (a persistently broken hardware manifests as 1 Hz error spam in errors.log — acceptable, documented).

### UI polish
- **Tray menu (`DarkMenu`):** Windows 11 rounded corners + DWM dark mode on the popup (`Native.StyleMenuFrame` at `HandleCreated`), more padding (menu 6, items 8/6), hover pill no longer touches the neighbor, separator with margins, one shared `Font` (also fixes a per-menu Font leak when the language changed).
- **Close dialog (redesigned):** real 40 px header with the title and a **visible X to cancel** (Choice stays null), DWM shadow without transparency (`GlassFrameThickness=-1`), body on Surface with themed buttons/checkbox (no more `Margin="-8"` hack), themed focus rings, focus given at `ContentRendered` (focus at `Loaded` is lost when the window activates).
- **Phantom frame fix (the "L" stripe the user saw):** `SizeToContent` + `WindowChrome` makes WPF size the window including a frame that no longer exists; the leftover shows the DWM frame fill (#2B2B2B). `CloseDialog.FixFrame` (at `Loaded`) switches `SizeToContent = Manual` BEFORE `SetWindowPos` — order matters, WPF reverts external resizes while SizeToContent is on — then shrinks the native window to the exact content size, re-centered. Pixel-verified at 125% DPI: 475×227, bottom/right rows are Surface #111111, focus ring present.

### How all of this was verified
- `dotnet build` 0 errors / 0 warnings after every step.
- `--render` smoke test: 32 PNGs (en+es, all pages, 6 overlay captures, colors), clean exit, zero new `errors.log` entries.
- Real-run dialog verification: launch, `PostMessage WM_CLOSE` to the main window (largest window of the PID), capture the new dialog HWND with a DPI-aware GDI+ utility, then pixel-classify the capture against the app palette (bottom/right rows must be `#111111`, not `#202023`; accent button ring at its edges). The two console utilities used (`imgan` pixel classifier, `shot` window capturer) were throwaway builds in `%TEMP%\opencode\` — recreate if needed.

---

## NOT done / backlog (prioritized for the next AI)

### RAM (the declared goal was to get far below ~106 MB)
1. **A1 — remove WinForms/System.Drawing from the tray** (biggest single remaining lever, −6..−12 MB): replace `NotifyIcon` + `DarkMenu` with `Shell_NotifyIcon` over an `HwndSource` (the hotkey window pattern already exists in `App.SetupHotkeys`) and a WPF/native popup menu. Medium risk (replicate menu/balloon behavior). Everything else structural was done; ICU must stay (WPF).
2. **A6:** build `Snapshot.AllSensors` only when the Sensors page / temp picker / web JSON needs it (still ~100–200 records + strings per tick).
3. **A7 (experiment):** `PublishReadyToRunExclude` for TraceEvent/LHM; measure resident pages vs startup time before keeping it.

### CPU
4. **B2:** replace per-tick `RaiseAll` with selective `Raise(...)` (265 MainWindow bindings + row `RaiseAll`s are still re-evaluated every second; values are mostly unchanged tick to tick).
5. **B4:** `ProcessMonitor` via one `NtQuerySystemInformation(SystemProcessInformation)` call instead of `Process.GetProcesses()` + per-PID OpenProcess (runs every 2 s while visible).
6. **B5:** `Describe()` for temperature choices runs per tick (`MainViewModel.Temps.cs`); only needed on creation/language change.
7. **B6:** `MultiChart` still builds 5 `FormattedText` Y-labels + 3 X-labels per render; cache them.

### Stability / hygiene
8. **C5:** autostart toggle is synchronous on the UI thread (`schtasks`, `WaitForExit(10000)` in `Autostart.cs`) — can freeze Settings for seconds with slow AV.
9. **C6:** SSE writes to a stalled client are unbounded (`Task.Run` per second, no write timeout/heartbeat) — cap in-flight writes, add keep-alive.
10. **C8:** `GpuProcessMonitor._broken = true` is permanent on one transient failure; add retry with backoff.
11. **C9 leftovers:** dispose `NotifyIcon.Icon`; dispose enumerated `ManagementObject`s in `DiskMap`; clean zombie rows when hardware disappears.

### Known cosmetics (accepted, documented)
- Opening the Network page as admin shows "needs admin" for ~1–2 s (session creation is deferred; first `SampleNetwork` is null).
- First SSE frame after connecting can be one tick stale; self-corrects in ≤1 s.
- Storage card shows "—" for ~1 s at startup (async drive read).
- `--render`: `ApplyScale(2)` is a no-op if the user's `OverlayScale` is exactly 2.0 (one PNG variant less); a future regression that measures 0 now manifests as a missing PNG (silent skip in `Save`).

## Carried over from previous notes (still pending)
- Push/tag a release (bump `<Version>`, `git tag vX.Y.Z && git push origin vX.Y.Z`), check the Actions run.
- Test in a real game: FPS stability (sliding-window logic is new-ish), "Start with Windows" in the elevated Release build, a real self-update.
- Code signing (removes Defender/SmartScreen warnings; the owner must do the submission).
- Vulkan/OpenGL games report no FPS (only DXGI/D3D9 present events are captured).

## Gotchas for the next AI (hard-won, don't relearn these)
1. **Never `InvariantGlobalization` in a WPF app.** Bindings resolve `xml:lang` to a concrete culture at layout time; without ICU every formatting binding throws `XamlParseException` inside `Window.Show()`, `ErrorLog` swallows it (`e.Handled = true`), and you get a **process alive with no window**. Check `%AppData%\HwPulse\errors.log` first when "the app doesn't open".
2. **`SizeToContent` + `WindowChrome` leaves a phantom frame**: WPF sizes the window including the removed frame. With `GlassFrameThickness=-1` (DWM shadow) the leftover shows as a #2B2B2B "L" stripe. Fix pattern: `SizeToContent = Manual` **before** `SetWindowPos` to the content size (external resizes are reverted while SizeToContent is on). See `CloseDialog.FixFrame`.
3. **Focus given at `Loaded` is lost** when the window activates; use `ContentRendered`.
4. **Detached visuals never measured**: the overlay's `_source` only gets measured in `OverlayWindow.Render()`, which early-returns while hidden. Anything capturing it must force a layout (`RenderNow`/measure) or it measures 0.
5. **`--render` never exits if `RenderScreenshots` throws**: the exception unwinds into PollLoop's catch, `ExitApp()` is skipped, and the app keeps running with the window open. Symptom: missing PNG + process alive.
6. **The pixel-level debug loop works without eyes on screenshots**: the user's ShareX captures live in `Pictures\ShareX\Screenshots\<month>\`; a small GDI+ tool that classifies pixels against the app palette (Bg/Surface/Line/Accent...) plus a FindWindow-by-PID + GetWindowRect + CopyFromScreen capturer lets you verify UI claims numerically (RGB rows, band sizes) even when image input is unavailable.
