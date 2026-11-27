# HwPulse: handoff notes

Status snapshot so another developer or AI agent can pick up the work. Last updated 2026-11-27, release 0.1.6 (AMD CPU sensor names, NVML GPU power fallback, honest total power, targeted PawnIO upgrade, better diagnostics; 0.1.5: PawnIO real-driver check, auto-start and diagnostics; 0.1.4: PawnIO auto-install; 0.1.3: in-window update notice; 0.1.2: backlog items C5, C6, C8, C9, B5, B6 and the close-dialog centering fix, on top of the 2026-11-26 optimization pass).

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
- **`--render <dir>`:** renders every page to PNG and writes `en-fit.txt`/`es-fit.txt`. Every page except Sensors must say `OK` (no scrolling). Expect **32 files** (30 PNG + `en-fit.txt` + `es-fit.txt`) and the process must **exit on its own**. If a PNG is missing, something measured 0 (see gotcha 5). Works even with the overlay hidden (`OverlayVisible: false` in `%AppData%\HwPulse\settings.json`).
- **Other dev flags:** `--editor` and `--move-mode` (DEBUG), `--native-probe <file>`, `--fps-probe <file>`, `--selftest-cpu <file>`, `--fake-update <ver>`, `--fake-pawnio <state>`, `--timing`, `--bench <file>`, `--dump <file>`, `--font "<name>"`, `--minimized`, `--no-elevate`, `--updated` (used internally after a self-update).
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

- **Close dialog off-center (fixed 2026-11-27):** the dialog opened shifted left, sometimes half outside the main window. Cause: `FixFrame` re-centered relative to the position WPF had picked via `CenterOwner`, and WPF computes that position with the phantom-frame size. `FixFrame` now centers explicitly on the owner's `GetWindowRect`, in physical pixels, and clamps to the monitor work area (`MonitorFromWindow`/`GetMonitorInfo`). If the owner is hidden or minimized, it centers on the monitor instead. Measured: center offset 0,0 in a normal window and with the main window half off-screen (dialog clamped inside).

### How all of this was verified
- `dotnet build` 0 errors / 0 warnings after every step.
- `--render` smoke test: 32 PNGs (en+es, all pages, 6 overlay captures, colors), clean exit, zero new `errors.log` entries.
- Real-run dialog verification: launch, `PostMessage WM_CLOSE` to the main window (largest window of the PID), capture the new dialog HWND with a DPI-aware GDI+ utility, then pixel-classify the capture against the app palette (bottom/right rows must be `#111111`, not `#202023`; accent button ring at its edges). The two console utilities used (`imgan` pixel classifier, `shot` window capturer) were throwaway builds in `%TEMP%\opencode\` — recreate if needed.

---

## DONE 2026-11-27 (0.1.7, test build, not published)

- **Generic CPU core recognition, so values no longer depend on exact sensor names.**
  - **Bug report:** a second friend with an **Intel Core i9-13900KF** (hybrid, 8 P-cores + 16 E-cores) had temperature but no clock. LHM names hybrid clocks `P-Core #N` / `E-Core #N`, which matched neither `CPU Core #N` (classic Intel) nor `Core #N` (AMD). Temperature worked through `Core Max`.
  - **One anchored pattern** (`CoreName`): `^(?:CPU\s+)?(?:(P|E)-)?Core #N(?:\s+Thread #M)?$`, case-insensitive. Derived sensors such as `(Effective)`, `VID` and `(SMU)` don't match because the pattern is anchored. The key is kind (plain, P or E) × 1000 + N. Display order: P first, then plain, then E.
  - **`PlanCores`** (static and testable) decides the physical cores and where each load goes:
    1. The load key equals a clock/temperature key (or an alias of one) → direct.
    2. Same count → by position.
    3. Twice as many loads → in pairs (AMD SMT).
    4. Weighted P=2 / E=1 threads → sequential (hybrid with per-thread loads).
    5. Otherwise each load key becomes its own core.
  - **Hybrid aliasing:** if there are as many plain keys as typed P/E keys (for example temperatures named `CPU Core #N` while clocks are P/E), the plain keys are aliased by position.
  - **Labels:** hybrid cores show as `Core P1`…`Core E16` (`CoreReading.Label`); others keep `Core N`.
- **FPS for Vulkan/OpenGL (DxgKrnl fallback)**, from an owner report that Minecraft with Vulkan showed no FPS.
  - `FpsMonitor` also enables `Microsoft-Windows-DxgKrnl` `{802EC45A-1E99-4B83-9920-87C98277BA9D}` with the `Present` keyword `0x8000000` only; the manifest was confirmed on the owner's PC with `wevtutil gp`.
  - It records per (pid, type) the events Blit 166, Flip 168, FlipMPO 252, PresentHistory_Start 171 and PresentHistoryDetailed_Start 215, skipping pid ≤ 4.
  - **Per candidate pid:** use DXGI/D3D9 when available (more precise). Otherwise use the kernel type with the MOST events in the 1 s window, never the sum, because one frame can emit several types.
  - Pruning and hold logic are shared (`Window`, `Prune`). If enabling DxgKrnl fails, DirectX keeps working.
  - **Dev flag `--fps-probe <file>`** (needs admin): listens for 5 s and writes the foreground pid plus per-process rates for each source.
  - **Verified on the owner's Minecraft (Vulkan) with `--fps-probe`:** `javaw` emits `Blit 327/s` + `PresentHistoryDetailed 327/s`, one of each per frame, and FPS now shows in game. HwPulse read 246 while NVIDIA's overlay read 219 at the same moment, and Minecraft's own F3 is within a few FPS of HwPulse.
    - Why NVIDIA differs: Minecraft Vulkan presents in **Blit** mode, so DWM composites at the monitor rate and drops extra frames. HwPulse counts frames the game submits (= F3), while NVIDIA's overlay counts closer to displayed frames. This is expected, not a bug.
  - **PresentHistory (171) fires TWICE per frame:** the probe showed Discord at `PresentHistory 44.5/s` vs `PresentHistoryDetailed 22.5/s` = DXGI 22.5/s. So the kernel type is chosen by fixed priority (`KernelPriority`: Flip, FlipMPO, PresentHistoryDetailed, Blit, then PresentHistory), not by highest count. PresentHistory is used only as a last resort, keeping every other timestamp.
- **Editor round 2 (owner feedback):**
  - **Header alignment:** rows have 6 px padding and the list ScrollViewer's bar steals width only from the rows, so `ElementsScroll_ScrollChanged` sets the header margin to 6 (row padding) + 6 (panel margin) + the visible scrollbar width. "Colors" and "Extras" headers span two columns.
  - **Animated drag reorder:** replaces Windows DragDrop, which shows no movement. Mouse capture on the ≡ handle; the dragged row gets a shadow, a SurfaceAlt background and z-index, and follows the cursor (TranslateTransform, clamped). The other rows slide out of the way with 140 ms ease-out animations; drop commits the reorder. Verified with real mouse input: mid-drag frame captured, and the saved order changed (fps moved from index 0 to 2).
  - **Sliders, all of them, app-wide:** `Controls/SliderDragFix.cs` registers a class handler. A click on the track (not the thumb) sets the value at the point, runs `UpdateLayout`, then raises MouseLeftButtonDown on the Thumb, so the same gesture keeps dragging. **Not verified with real input:** an automated test sent clicks to the owner's desktop because the editor wasn't foreground, so automated mouse tests on the owner's PC were stopped. The owner must test this by hand.
  - **Positions:** the `OverlayCorner` enum gained `BottomCenter` and `Custom` (appended, so saved values are unchanged). Settings shows a 3×2 grid (top L/C/R, bottom L/C/R). The hotkey cycles TL→TC→TR→BR→BC→BL. `Custom` uses `AppSettings.OverlayX/OverlayY` (fractions of the screen, clamped on screen); Settings shows "Custom position" when it's active.
  - **New editor features:**
    - **Mini graph per element** (`OverlayElement.Graph`, keys listed in `OverlayLayout.Series`: fps, cpuLoad, cpuTemp, cpuClock, gpuLoad, gpuTemp, ram) uses SparkChart bound to the VM series. In column layout the graph replaces the bar. The series were added to `OverlayProps` for tray mode.
    - **Undo/redo:** a JSON-snapshot history of up to 100 steps; changes within 600 ms are merged into one step (slider drags, typing). Buttons use Segoe MDL2 icons; Ctrl+Z / Ctrl+Y work except while a TextBox has focus.
    - **"Only show while playing"** (`AppSettings.OverlayOnlyInGame`, toggle in the editor's Behavior section, applies to every style): the overlay gets Opacity 0 and skips rendering when `FpsActive` is false, with FpsMonitor's 2.5 s hold. While this option is on, the FPS ETW session is kept on even if the overlay shows no FPS item.
    - **"Move with the mouse"** (editor toggle, `OverlayWindow.SetMoveMode`): removes WS_EX_TRANSPARENT, shows a translucent blue highlight and a SizeAll cursor, and `DragMove()` saves a Custom position. It turns off when toggled off or when the editor closes.
      - **Gotcha:** the editor opens with `ShowDialog()`, which disables every other window of the app, overlay included. `SetMoveMode(true)` therefore also calls `Native.EnableWindow(hwnd, true)`; without it the overlay never got the click. That was the bug the owner reported.
      - **Verified without moving the mouse:** the DEBUG flag `--move-mode` (together with `--editor`) turns the mode on after 4 s, and an external check read the overlay as enabled=True and WS_EX_TRANSPARENT=False.
      - **Lesson:** never run synthetic mouse tests on the owner's PC. They hit the real desktop when the target window isn't in front.
- **Web dashboard "keep screen on" toggle** (sun icon in the header, remembered in localStorage `hwpAwake`, on by default): it uses the Wake Lock API when the page is a secure context (localhost or https). Otherwise, for example a phone on `http://192.168.x.x` where the API doesn't exist, it falls back to a muted 2×2 `<video>` playing a `canvas.captureStream(1)` that redraws every second. It re-acquires on `visibilitychange` and on the first tap. Buttons use `-webkit-tap-highlight-color: transparent` (the owner saw a borderless box flash on tap) and a focus-visible outline instead of the default focus box. Verified in the built-in browser: toggling works, no console errors, and the fallback video plays. **Not verified on real phones**; the fallback is best-effort, and iOS may still sleep over plain http.
- **Fully customizable overlay (visual editor)**, requested by the owner, who chose the "visual editor" option.
  - **New overlay style "Custom"** (`AppSettings.OverlayStyle = "custom"`), alongside Panel and Single line. It's selectable in Settings → Overlay; in Custom, the "Customize colors" button and the "Show in overlay" checkboxes are replaced by **"Edit design…"**.
  - **Model `Core/OverlayLayout.cs`** (stored in `AppSettings.OverlayLayout`):
    - **Global:** Arrangement (one line / column / grid with N columns), Font (whitelist), background color and opacity, border color, opacity and width, corner radius, X/Y padding, spacing, separator text and color.
    - **Per element:** Key, Visible, Label, ValueColor ("auto" = dynamic temperature/FPS colors, or #RRGGBB), LabelColor, Size, Bold, Bar (only for % keys).
    - **Catalog:** fps, frametime, cpuLoad/Temp/Clock/Power, gpuLoad/Temp/Clock/Power, vram, ram, power (total), time. New VM property `ClockTimeText`; the extra properties were added to `OverlayProps` so tray mode keeps them updated.
    - **Presets:** line (classic), minimal, vertical (column with bars), bigfps, grid.
    - **Validation:** `Normalize()` clamps every number, whitelists the font and keys, accepts only #RRGGBB colors, and limits label and separator text. It runs on load and on import, because an elevated process reads files that others may share.
  - **`Views/OverlayCustomBuilder.cs`** builds the visual tree from the layout. It binds to the same VM properties, so values update every second; scale multiplies sizes (no transform), and the shared-size column aligns labels in vertical mode. `OverlayWindow` has a `CustomHost` ContentControl; `RebuildCustom()` runs on layout or scale changes (from `ApplyScale`), and the 2× supersampled rendering pipeline is unchanged.
  - **`Views/OverlayEditorWindow`:**
    - Left side: live preview over a "game-like" gradient, plus global style controls.
    - Right side: the element list. Each row has drag ≡ or ▲▼ to reorder, a visible switch, the label text, value and label color swatches, size, a Bold toggle and a Bar toggle.
    - Header: Presets menu, Import… / Export… (.json, import limited to 64 KB plus `Normalize`) and Done.
    - Every edit applies live to both the preview and the real overlay (`MainViewModel.SetCustomLayout` → `OverlayLayoutChanged` → rebuild + save).
  - **`Views/ColorPickDialog`:** a reusable picker (16 quick colors, HEX, HSL sliders, optional "Automatic"). It previews live and Cancel reverts.
  - **Dev:**
    - `--render` now also writes `overlay-custom-<preset>.png` (the window image) and `overlay-editor.png`, then restores the user's style and layout.
    - `--editor` (DEBUG only) opens the editor at startup.
    - Note: `--render` window captures via RenderTargetBitmap can show some text faint; the live window was verified with a real screenshot.
  - **Not done / ideas:** drag the overlay position freely (corners only today), a per-element mini graph (sparkline), per-element custom formats and units.
- **Windows built-in fallbacks (`Monitoring/NativeSensors.cs`)** need no driver and no admin, and never touch games. They are used only when LHM gives no value (PawnIO blocked by an anti-cheat, no admin, unsupported hardware).
  - **CPU clock:** PerformanceCounter "Processor Information(_Total)" `% Processor Performance` × `Processor Frequency`, the same source as Task Manager. It's the last step of the `CpuClockAvg` chain. Verified non-admin on the owner's PC: 4195 MHz, where LHM gives nothing without admin.
  - **GPU temperature / fan / power %** for any vendor (WDDM 2.4+): `D3DKMTEnumAdapters2` → `D3DKMTQueryAdapterInfo` with `KMTQAITYPE_ADAPTERREGISTRYINFO` (8, for the adapter name) and `KMTQAITYPE_ADAPTERPERFDATA` (**62**, value confirmed empirically). The struct is `D3DKMT_ADAPTER_PERFDATA` (Temperature in 0.1 °C, Power in 0.1 % of max, FanRPM). Zero means "not implemented".
    - The adapter is matched by name to the LHM GPU, and its values fill `GpuTemp` / `GpuFanRpm` / the new `Snapshot.GpuPowerPct` when LHM lacks them. The UI shows power as "NN %" when there are no watts.
    - Verified on the owner's GTX 1660 Ti: 54.1 °C, matching LHM. That laptop driver reports no fan or power.
  - **Dev flag `--native-probe <file>`** prints what these sources return. `--dump` also prints a "Windows counters" line.
  - **Not considered and rejected:** the ACPI Thermal Zone counter (system or motherboard temperature, not the CPU), HWiNFO shared memory / AIDA64 (not free or time-limited), and RTSS-style injection (anti-cheat risk).
- **Researched next step:** LibreHardwareMonitor **0.9.7 previews** on NuGet (`0.9.7-pre785`) carry 147 commits since 0.9.6: Intel Panther Lake and Arrow Lake-U, AMD Fire Range, Zen 5 CCD temperatures, a Zen temperature-offset fix, RAM temperatures, NVIDIA hotspot, NVIDIA driver-restart and NVML fixes, NVMe temperature fixes, and many motherboard EC/Super I/O additions. Test it with friends' `--dump` output before switching, or wait for 0.9.7 stable.
- **Generic fallbacks:**
  - `CpuClockAvg`: per-core average → `Cores (Average)` → `AvgCpuClock`, which averages every CPU clock except Bus, Effective, Average, Ring, Uncore, Memory, Fabric and Graphics.
  - `CpuPower` falls back to the first Power sensor containing "package".
  - `CpuVoltage` falls back to the first Voltage sensor containing "core".
  - Temperature keeps the earlier chain ending in `MaxCpuTemp`.
- **UI:** the per-core list now has its own scroll (`MaxHeight=192`, 8 rows), so a 24-core i9 doesn't stretch the CPU page.
- **Dev flag `--selftest-cpu <file>`:** runs `PlanCores` on the real LHM name sets of the i5-10300H, Ryzen 5 5500 and three hybrid naming variants. Result: 4, 6 and 24 cores with correct thread counts (P:2, E:1) and no unmapped loads.
- **Settings fit:** a pre-existing 3 px overflow appeared because the Web dashboard text now wraps to 3 lines; it reproduced without these changes. Fixed by shrinking the overlay slider rows from 34 to 30 px and the QR top margin from 12 to 8 px. Settings is now 626/631 in both languages.
- **Verified:** build 0 warnings; render shows every page OK; the owner's Intel is unchanged (4 cores). **The i9 itself is not verified yet:** wait for the friend's confirmation and, if anything is off, ask for their `--dump`.

---

## DONE 2026-11-27 (0.1.6)

- **REAL ROOT CAUSE of "no CPU temperature/clock" on the friend's AMD Ryzen 5 5500 (found from their `--dump`):** LHM read every sensor fine (the Sensors page listed them, and PawnIO was **2.2.0** and working), but HwPulse only mapped **Intel** sensor names. The PawnIO-version hypothesis below was wrong. The targeted upgrade stays as a harmless safety net, since it only fires with no clock AND PawnIO < 2.2.0.

  | Value | Intel (LHM name) | AMD Ryzen (LHM name) |
  |---|---|---|
  | CPU temperature | `Core Max`, `CPU Core #N` | `Core (Tctl/Tdie)` (also `Core (Tctl)`, `CPU Cores`, `CCDn (Tdie)`) |
  | Per-core clock | `CPU Core #N` | `Core #N` (physical cores only) |
  | Per-core load | `CPU Core #N Thread #M` | `CPU Core #1..#12`, one per **logical thread** on a 6c/12t part |
  | Power | `CPU Package` | `Package` |
  | Voltage | `CPU Core` | `Core (SVI2 TFN)` / `(SVI3 TFN)` |

  - `SensorService` now accepts the AMD names: the `AmdCoreClock` regex; `CpuTempMax` falls back through the AMD names and finally `MaxCpuTemp` (highest CPU temperature that isn't a "Distance"); `CpuPower` and `CpuVoltage` fall back too; `CpuClockAvg` falls back to `Cores (Average)`.
  - **Thread → core pairing:** `_threadsPerCore` = 2 when there are exactly twice as many load sensors as clock sensors (max index too), so loads `2k-1` and `2k` average into core `k`. Intel stays at 1, because its threads are already named `Thread #M`. The core order and divisor are computed once per map version, BEFORE the per-tick loop.
  - **Temperature picker:** `Core (Tctl/Tdie)` / `Core (Tctl)` / `CPU Cores` become "CPU (Tctl/Tdie)", checked by default; `CCDn (Tdie)` sensors are listed unchecked.
  - **Verified:** Intel is unchanged (4 cores, every page OK, no errors). AMD still needs a real test on the friend's PC, since no AMD machine is available for development.
  - **The friend confirmed AMD works** (temperature, clock and power now show).
- **NVIDIA GPU power fallback (`Monitoring/NvmlPower.cs`):** the friend's RTX 4060 dump has NO power sensor (no `GPU Package`, and NvAPI "Power Topology" is empty). LHM's NVML read fails silently on that PC, while on the owner's GTX 1660 Ti `GPU Package` comes from NVML.
  - When LHM gives no GPU power and the GPU is NVIDIA, `SensorService.FillGpu` calls `NvmlPower.ReadWatts(gpu.Name)` (`nvmlDeviceGetPowerUsage`, mW → W).
  - It loads `nvml.dll` with `NativeLibrary.Load`, only from admin-writable places: System32, then the newest `DriverStore\FileRepository\nv*\nvml.dll`, then `Program Files\NVIDIA Corporation\NVSMI`.
  - It picks the device by name ("NVIDIA " prefix tolerated), otherwise index 0.
  - Each failure step logs once to errors.log (`NVML:` dll not found / `nvmlInit_v2` code / `GetPowerUsage` code).
  - `--dump` prints `NVML (direct): X W`. Verified on the owner's PC: 20.2 W, consistent with LHM's 13.9–20.5 W.
  - **Result on the 4060:** `nvmlDeviceGetPowerUsage` returned code 3 (`NVML_ERROR_NOT_SUPPORTED`), and `nvidia-smi` reports `power.draw`, `power.draw.average` and `power.draw.instant` as `[N/A]` (driver 621.14; `power.limit` = 115 W). That GPU/driver combination does not report power at all, so no tool can show it. Not a HwPulse bug; the fallback stays for other machines.
- **Clean CI after 0.1.6 (committed after the v0.1.6 tag, ships with the next release):**
  - The workflow uses `actions/checkout@v7` and `actions/setup-dotnet@v6` (Node 24). The v4 actions caused the "Node.js 20 is deprecated" warning.
  - `Updater.IsSingleFile` now checks whether `HwPulse.dll` sits next to the exe (`AppContext.BaseDirectory`) instead of `Assembly.Location` being empty, which removes the IL3000 warning.
  - Verified: 0 warnings in the single-file and folder publishes, and detection is correct in both.
- **Honest total power:** when a GPU exists but reports no power, the Power card label becomes "Total (CPU only)" (`TotalLabel`) instead of presenting the CPU watts as the system total. The GPU power row and the GPU page "Power" readout explain "Not reported by this GPU or its driver" (`GpuPowerTip` as tooltip / sub-text).


- **Field report from the friend's PC (0.1.5 diagnostic line):** `CPU='AMD Ryzen 5 5500'; clock=none; PawnIO: uninstallEntry=True, service=unknown, device=True; HVCI=not set; OS=10.0.26300`.
  - PawnIO is installed and its device exists, yet BOTH temperature and clock are missing. On AMD, both come from the same LHM PawnIO modules (`AMDFamily17.bin` / `RyzenSMU.bin`), so the module most likely fails to load.
  - **Leading hypothesis:** an older PawnIO, installed by another tool, that lacks natives the LHM 0.9.6 modules use. The owner's Intel PC works with PawnIO 2.1.0.0, but Intel uses a different module (`IntelMSR.bin`). **Unconfirmed**; the friend's PawnIO version wasn't logged in 0.1.5.
- **Targeted upgrade, tried once:**
  - Conditions, checked at the ~8 s diagnostic: admin, no CPU temperature AND no CPU clock, PawnIO device present, installed PawnIO version below `PawnIOInstaller.Pinned` (2.2.0), and `!AppSettings.PawnIOUpgradeTried`.
  - It then runs the pinned installer through `EnsurePawnIOAsync(upgrade: true)`, with the notice and relaunch.
  - Machines where the CPU already reads fine (like the owner's) are never touched.
- **Diagnostic fixes:**
  - `ServiceState()` no longer depends on the system language. It used to look for the localized "STATE" label (Spanish Windows says "ESTADO"); now it matches the untranslated state names (RUNNING, STOPPED…).
  - The `CPU diagnostics` line now includes the PawnIO `version` (Uninstall `DisplayVersion`).
- **`--dump <file>`** now appends a `PawnIO:` status line and LHM's full `Computer.GetReport()`. To use it on another PC, run `HwPulse.exe --dump "$env:USERPROFILE\Desktop\hwpulse-dump.txt"` and accept UAC; the dump relaunches elevated.
- **Next if 0.1.6 doesn't fix the friend's PC:** read their dump and check the AMD CPU section of the LHM report for module or SMU errors. Also compare against the official LibreHardwareMonitor app on that PC, and look for software holding LHM's global mutexes (Ryzen Master, Armoury Crate, iCUE…).

---

## DONE 2026-11-27 (0.1.5)

- **The PawnIO check now tests whether the driver actually works, not just whether it is registered.**
  - **Bug report:** a friend on 0.1.4 had no CPU temperature, no PawnIO notice and an empty log.
  - **Root cause:** 0.1.4 only checked `Services\PawnIO`. Inspecting the LHM 0.9.6 binary shows that it gates on `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO`, then opens `\?\GLOBALROOT\Device\PawnIO`. A registered service with the driver not loaded (stopped, or a broken leftover install) therefore gives silent "—" values, and 0.1.4 skipped installing.
- **`PawnIOInstaller.IsWorking()`** = the Uninstall entry exists AND `DeviceAvailable()`. `DeviceAvailable()` calls `CreateFile` with `FILE_READ_ATTRIBUTES`: error 2, 3 or 161 means missing; anything else, including 5 (access denied for non-admin), means the device exists. Verified on the dev PC: real device → 5, fake path → 2.
- **`TryStartDriver()`** runs inside the sensor task BEFORE `new SensorService` (LHM only checks PawnIO when it opens). If the service exists but the device doesn't, it runs `sc start PawnIO` and waits up to 3 s for the device.
- **`EnsurePawnIOAsync`** now runs the installer whenever `!IsWorking()`, so a broken install gets reinstalled. After an exit code of 0 it re-checks that the driver works; if not, the run counts as failed (no install loop thanks to `PawnIOAutoFailed`).
- **Diagnostics:** `ErrorLog.Info` is new. About 8 s after startup, if the app is elevated and there is no CPU temperature, it logs one `CPU diagnostics:` line with the CPU name, clock, PawnIO uninstallEntry / service state (`sc query`) / device, HVCI `Enabled`, OS version and HwPulse version. Use it for unsupported CPUs, HVCI or anti-cheat blocking, and so on.
- **Not verified elevated:** `TryStartDriver`, the diagnostic line and the reinstall path need admin. The dev runs were non-admin.

---

## DONE 2026-11-27 (0.1.4)

- **PawnIO is installed automatically** (`Core/PawnIOInstaller.cs`, `App.EnsurePawnIOAsync`).
  - **Cause it fixes:** LibreHardwareMonitorLib 0.9.6 needs the PawnIO driver for CPU temperature, clock and power, but never installs it. Without it those values stay "—" and nothing is logged. This was reported by a friend of the owner. The owner's PC had PawnIO 2.1.0 installed separately (probably by LibreHardwareMonitor).
  - **Detection:** the registry key `HKLM\SYSTEM\CurrentControlSet\Services\PawnIO` exists. If the registry can't be read, the app assumes PawnIO is installed and doesn't try.
  - **Flow:**
    1. After the sensors open, only when elevated and not in `--render`, a notice appears: "Installing the PawnIO driver…".
    2. The app downloads the official installer, pinned to v2.2.0: `https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe`. Its SHA-256 (`1F519A22…3032`) matches the winget manifest, and its Authenticode signer is CN=namazso.eu.
    3. The file stays open without share-write/delete while it is hashed and run, so it can't be swapped (TOCTOU).
    4. It runs `-install -silent`. Exit code 0 means installed: the notice says "Restarting…" and the app relaunches through `Updater.Relaunch` (`--updated`) so LHM reopens with the driver. Exit code 3010 shows "Restart Windows…".
  - **On failure:** the error goes to `errors.log`, `AppSettings.PawnIOAutoFailed = true`, and the notice offers Retry, Install manually (pawnio.eu) and ×. The install is not retried automatically on later launches. The × sets `PawnIODismissed` and the notice never returns.
  - **Updating PawnIO:** bump `Version`, `Url` and `Sha256` together in `PawnIOInstaller.cs`. Take the hash from winget-pkgs `manifests/n/namazso/PawnIO/<ver>/namazso.PawnIO.installer.yaml`.
  - **Dev flag:** `--fake-pawnio installing|done|reboot|failed` shows the notice state; it never installs anything.
  - **Not verified end to end:** the real silent install was not run, because the dev PC already has PawnIO 2.1 and running it would upgrade the owner's driver. Download and hash were verified manually. Test on a PC without PawnIO.

---

## DONE 2026-11-27 (0.1.3)

- **In-window update notice.** When a newer release is found, a notice shows at the top of every page, in the same slot and style as the "no admin" notice but with an accent stripe.
  - Text: "A new version is available: **HwPulse X**".
  - Buttons: **Update now** (same flow as the Settings link; shows "Downloading…" while busy and "Retry" if it fails), **What's new** (opens the release page) and **×**.
  - The × stores `AppSettings.UpdateDismissed` = that version, so the notice doesn't return until a newer one exists.
  - The tray balloon still fires once per version; its text now says "Open HwPulse to install it".
  - VM: `ShowUpdateBanner`, `UpdateBannerVisibility/Version/Action` in `MainViewModel.Behavior.cs`. App: `DismissUpdateBanner`, `OpenUpdateNotes`.
- **Dev flag `--fake-update <version>`** simulates an available update so you can see the notice without publishing anything; it never downloads. Combine it with `--render`.
- **Layout note:** with BOTH the "no admin" notice and the update notice (only possible in non-admin dev runs), Overview, CPU, GPU and Settings scroll by 20–40 px. In normal use the app is elevated, so only one notice shows and every page fits.

---

## DONE 2026-11-27 (0.1.2)

- **Git:** the opencode pass had created its own local repository without a remote. Its baseline tree was identical to `origin/main` (0.1.1), so its commits were rebased onto `origin/main` and pushed. The local branch is now `main`, tracking `origin/main`. The old local history is kept in branch `backup-local-history` (local only, safe to delete).
- **Close dialog centered:** see "Close dialog off-center" under UI polish.
- **C5:** the "Start with Windows" switch no longer blocks the UI. The initial `schtasks /Query` and every create/delete run in `Task.Run`. The switch updates optimistically, is disabled while busy (`StartWithWindowsEnabled`), and reverts to the real state if creation fails. Before, the getter ran `schtasks` synchronously the first time Settings was bound.
- **B5:** temperature labels (`Describe`) are recomputed only when the language, the CPU/GPU names or the disk short names change. `DescribeSignature` hashes those inputs; before, it ran for every sensor every second.
- **C8:** `GpuProcessMonitor` retries after a failure instead of switching off for the whole session. Backoff is 30 s, 60 s and so on up to 10 min, the category handles are reopened, and only the first error is logged.
- **C6:** each SSE client has at most one write in flight (`StreamClient.Busy`, Interlocked). A busy client skips frames instead of queueing them, and a write slower than 5 s drops the client (the browser reconnects through `retry: 2000`). The initial frame goes through the same path. Tested: with a deliberately stalled client connected (`curl --limit-rate 1`), a healthy client still got about 1 frame/s.
- **C9:**
  - `DiskMap` disposes the `ManagementObjectCollection` and every `ManagementObject`.
  - The tray `NotifyIcon.Icon` is disposed on exit, except the shared `SystemIcons` fallback.
  - Sensor rows and groups of hardware that disappears are removed from the Sensors page; this is checked only when the sensor count changes.
  - Temperature choices of missing hardware are removed from the picker. The saved selection keeps their keys, so a reconnected disk comes back checked.
- **B6:** `MultiChart` caches axis `FormattedText` by text, invalidated when DPI changes or after 64 entries, and no longer uses LINQ for the Y labels.
- **Verified:**
  - Build: 0 errors, 0 warnings.
  - `--render`: 30 PNGs, every page OK, no new `errors.log` entries.
  - Close dialog: center offset 0,0, normal and with the main window half off-screen.
- **Performance (Release, 60 s after startup, overlay and web on):** about 0.72% CPU with the window open and 0.52% in the tray. Private memory about 161 MB visible and 119 MB in the tray, stable. Same range as 0.1.1; run-to-run noise is about ±0.1%.

---

## NOT done / backlog (prioritized for the next AI)

### RAM (the declared goal was to get far below ~106 MB)
1. **A1 — remove WinForms/System.Drawing from the tray** (biggest single remaining lever, −6..−12 MB): replace `NotifyIcon` + `DarkMenu` with `Shell_NotifyIcon` over an `HwndSource` (the hotkey window pattern already exists in `App.SetupHotkeys`) and a WPF/native popup menu. Medium risk (replicate menu/balloon behavior). Everything else structural was done; ICU must stay (WPF).
2. **A6:** build `Snapshot.AllSensors` only when the Sensors page / temp picker / web JSON needs it (still ~100–200 records + strings per tick).
3. **A7 (experiment):** `PublishReadyToRunExclude` for TraceEvent/LHM; measure resident pages vs startup time before keeping it.

### CPU
4. **B2:** replace per-tick `RaiseAll` with selective `Raise(...)` (265 MainWindow bindings + row `RaiseAll`s are still re-evaluated every second; values are mostly unchanged tick to tick).
5. **B4:** `ProcessMonitor` via one `NtQuerySystemInformation(SystemProcessInformation)` call instead of `Process.GetProcesses()` + per-PID OpenProcess (runs every 2 s while visible).
6. ~~**B5**~~ done in 0.1.2.
7. ~~**B6**~~ done in 0.1.2.

### Stability / hygiene
8. ~~**C5**~~ done in 0.1.2.
9. ~~**C6**~~ done in 0.1.2. There is no separate keep-alive; the 1 s data frames serve as one.
10. ~~**C8**~~ done in 0.1.2.
11. ~~**C9**~~ done in 0.1.2.

### Known cosmetics (accepted, documented)
- Opening the Network page as admin shows "needs admin" for ~1–2 s (session creation is deferred; first `SampleNetwork` is null).
- First SSE frame after connecting can be one tick stale; self-corrects in ≤1 s.
- Storage card shows "—" for ~1 s at startup (async drive read).
- `--render`: `ApplyScale(2)` is a no-op if the user's `OverlayScale` is exactly 2.0 (one PNG variant less); a future regression that measures 0 now manifests as a missing PNG (silent skip in `Save`).

## Carried over from previous notes (still pending)
- Test the PawnIO auto-install on a PC that doesn't have it (the friend's PC).
- Test a real self-update (open 0.1.2: the notice should offer 0.1.3; "Update now" should replace the exe and restart).
- Test in a real game: FPS stability (sliding-window logic is new-ish), "Start with Windows" in the elevated Release build, a real self-update.
- Code signing (removes Defender/SmartScreen warnings; the owner must do the submission).
- Vulkan/OpenGL FPS: DxgKrnl fallback added in 0.1.7; confirm with `--fps-probe` in a Vulkan game.

## Gotchas for the next AI (hard-won, don't relearn these)
1. **Never `InvariantGlobalization` in a WPF app.** Bindings resolve `xml:lang` to a concrete culture at layout time; without ICU every formatting binding throws `XamlParseException` inside `Window.Show()`, `ErrorLog` swallows it (`e.Handled = true`), and you get a **process alive with no window**. Check `%AppData%\HwPulse\errors.log` first when "the app doesn't open".
2. **`SizeToContent` + `WindowChrome` leaves a phantom frame**: WPF sizes the window including the removed frame. With `GlassFrameThickness=-1` (DWM shadow) the leftover shows as a #2B2B2B "L" stripe. Fix pattern: `SizeToContent = Manual` **before** `SetWindowPos` to the content size (external resizes are reverted while SizeToContent is on). See `CloseDialog.FixFrame`.
2b. **Don't trust `CenterOwner` together with `SizeToContent` + `WindowChrome`**: WPF places the window using the size that includes the phantom frame. Center the window yourself in physical pixels after shrinking it (see `CloseDialog.FixFrame`).
3. **Focus given at `Loaded` is lost** when the window activates; use `ContentRendered`.
4. **Detached visuals never measured**: the overlay's `_source` only gets measured in `OverlayWindow.Render()`, which early-returns while hidden. Anything capturing it must force a layout (`RenderNow`/measure) or it measures 0.
5. **`--render` never exits if `RenderScreenshots` throws**: the exception unwinds into PollLoop's catch, `ExitApp()` is skipped, and the app keeps running with the window open. Symptom: missing PNG + process alive.
6. **The pixel-level debug loop works without eyes on screenshots**: the user's ShareX captures live in `Pictures\ShareX\Screenshots\<month>\`; a small GDI+ tool that classifies pixels against the app palette (Bg/Surface/Line/Accent...) plus a FindWindow-by-PID + GetWindowRect + CopyFromScreen capturer lets you verify UI claims numerically (RGB rows, band sizes) even when image input is unavailable.
