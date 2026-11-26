# HwPulse: handoff notes

Status snapshot so another developer or AI agent can pick up the work. Last updated 2026-11-27, release 0.1.5 (PawnIO real-driver check, auto-start and diagnostics; 0.1.4: PawnIO auto-install; 0.1.3: in-window update notice; 0.1.2: backlog items C5, C6, C8, C9, B5, B6 and the close-dialog centering fix, on top of the 2026-11-26 optimization pass).

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
- **Other dev flags:** `--fake-update <ver>`, `--fake-pawnio <state>`, `--timing`, `--bench <file>`, `--dump <file>`, `--font "<name>"`, `--minimized`, `--no-elevate`, `--updated` (used internally after a self-update).
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
- Vulkan/OpenGL games report no FPS (only DXGI/D3D9 present events are captured).

## Gotchas for the next AI (hard-won, don't relearn these)
1. **Never `InvariantGlobalization` in a WPF app.** Bindings resolve `xml:lang` to a concrete culture at layout time; without ICU every formatting binding throws `XamlParseException` inside `Window.Show()`, `ErrorLog` swallows it (`e.Handled = true`), and you get a **process alive with no window**. Check `%AppData%\HwPulse\errors.log` first when "the app doesn't open".
2. **`SizeToContent` + `WindowChrome` leaves a phantom frame**: WPF sizes the window including the removed frame. With `GlassFrameThickness=-1` (DWM shadow) the leftover shows as a #2B2B2B "L" stripe. Fix pattern: `SizeToContent = Manual` **before** `SetWindowPos` to the content size (external resizes are reverted while SizeToContent is on). See `CloseDialog.FixFrame`.
2b. **Don't trust `CenterOwner` together with `SizeToContent` + `WindowChrome`**: WPF places the window using the size that includes the phantom frame. Center the window yourself in physical pixels after shrinking it (see `CloseDialog.FixFrame`).
3. **Focus given at `Loaded` is lost** when the window activates; use `ContentRendered`.
4. **Detached visuals never measured**: the overlay's `_source` only gets measured in `OverlayWindow.Render()`, which early-returns while hidden. Anything capturing it must force a layout (`RenderNow`/measure) or it measures 0.
5. **`--render` never exits if `RenderScreenshots` throws**: the exception unwinds into PollLoop's catch, `ExitApp()` is skipped, and the app keeps running with the window open. Symptom: missing PNG + process alive.
6. **The pixel-level debug loop works without eyes on screenshots**: the user's ShareX captures live in `Pictures\ShareX\Screenshots\<month>\`; a small GDI+ tool that classifies pixels against the app palette (Bg/Surface/Line/Accent...) plus a FindWindow-by-PID + GetWindowRect + CopyFromScreen capturer lets you verify UI claims numerically (RGB rows, band sizes) even when image input is unavailable.
