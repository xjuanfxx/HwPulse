# Changelog

All notable changes to HwPulse. The release workflow publishes the section of each version as its GitHub release notes.

## [0.1.7] - 2026-11-27

### What's new

**Fully customizable overlay (visual editor)**
- New overlay style, **Custom**. Choose it in Settings → Overlay → Style and open **Edit design…**.
- **Every value is configurable.** There are 14 values: FPS, frametime, CPU/GPU usage, temperature, clock and power, VRAM, RAM, total power and the time. For each one you can:
  - show or hide it;
  - reorder it by **dragging** (animated) or with ▲▼;
  - set its own label, value color (or *Automatic*: green → amber → red for temperatures and FPS), label color, size and bold;
  - add a **progress bar** (percentage values) and a **mini graph** of the last minute.
- **Whole design:**
  - layout as one line, a column or a grid with N columns;
  - font (8 choices);
  - background and border color and opacity, border width and rounded corners;
  - padding, spacing, and the separator between values (`|`, `·`, `/`…).
- **Presets:** Single line (classic), Minimal, Column with bars, Big FPS, Grid.
- **Export / import** designs as `.json` files, so users can share them. Imported files are fully validated.
- **Undo / redo** with buttons or Ctrl+Z / Ctrl+Y. It also covers the overlay position.
- **Cancel** reverts everything since the editor was opened; **Done** keeps it.
- Every change shows live in the preview and in the real overlay, and is saved automatically.

**Overlay**
- **Move with the mouse:** turn it on in the editor and drag the overlay anywhere on the screen. **Default position** puts it back at the top center.
- **Bottom center** position. Positions are now shown as a 3×2 grid, and `Ctrl+Shift+F11` cycles through all six.
- **Only show while playing:** the overlay hides itself, and costs nothing, when no game is drawing.

**FPS**
- **Vulkan and OpenGL games now show FPS** (Minecraft with Vulkan, emulators, DXVK…). They are read from the Windows graphics kernel present events, without touching the game, so it's safe with anti-cheats.

**Hardware support**
- **CPU cores are recognized generically:** classic Intel, **hybrid Intel (P-cores / E-cores, e.g. i9-13900KF)** and AMD Ryzen. Per-thread loads are grouped into physical cores; hybrid CPUs show "Core P1… E16". The core list has its own scroll for 24-core CPUs.
- **Windows built-in fallbacks** need no driver and no admin, and are used only when the sensor library has no value:
  - real **CPU clock** from Windows performance counters (the same source as Task Manager);
  - **GPU temperature, fan and power %** for any brand.
- Generic fallbacks for CPU clock, power and voltage when a CPU uses sensor names HwPulse doesn't know.

**Web dashboard**
- **Keep screen on** button (sun icon), on by default. It uses the Wake Lock API on `localhost`/`https`. On a phone opening the LAN address it falls back to a tiny muted video that keeps the screen awake (works on most Android phones; iPhone over plain http may still sleep).
- No more box flashing when tapping buttons on phones.

**Interface**
- Sliders can be dragged from anywhere on the track, not only from the thumb, everywhere in the app.
- Settings fits the window again without scrolling.

**Diagnostics for support** (command line)
- `--fps-probe <file>`: FPS events per process and source.
- `--native-probe <file>`: what the Windows fallbacks return.
- `--selftest-cpu <file>`: how CPU cores are recognized.

### Novedades (español)

**Overlay totalmente personalizable (editor visual)**
- Nuevo estilo **Personalizado**. Elígelo en Ajustes → Overlay → Estilo y abre **Editar diseño…**.
- **Cada dato es configurable.** Son 14 datos: FPS, frametime, uso, temperatura, reloj y potencia de CPU y GPU, VRAM, RAM, potencia total y la hora. En cada uno puedes:
  - mostrarlo u ocultarlo;
  - reordenarlo **arrastrando** (con animación) o con ▲▼;
  - ponerle etiqueta propia, color del valor (o *Automático*: verde → ámbar → rojo en temperaturas y FPS), color de etiqueta, tamaño y negrita;
  - añadirle **barra de progreso** (valores en %) y **mini gráfica** del último minuto.
- **Diseño completo:**
  - disposición en una línea, en columna o en rejilla de N columnas;
  - fuente (8 opciones);
  - fondo y borde con color y opacidad, grosor del borde y esquinas redondeadas;
  - márgenes, separación y separador entre datos (`|`, `·`, `/`…).
- **Presets:** Una línea (clásico), Minimalista, Columna con barras, FPS grande y Rejilla.
- **Exportar e importar** diseños en archivos `.json` para compartirlos. Al importar se valida todo.
- **Deshacer / rehacer** con botones o Ctrl+Z / Ctrl+Y. También deshace los cambios de posición del overlay.
- **Cancelar** deshace todo lo hecho desde que se abrió el editor; **Listo** lo guarda.
- Cada cambio se ve en vivo en la vista previa y en el overlay real, y se guarda solo.

**Overlay**
- **Mover con el ratón:** actívalo en el editor y arrastra el overlay a cualquier parte de la pantalla. **Posición por defecto** lo devuelve arriba al centro.
- Nueva posición **Abajo centro**. Las posiciones ahora van en una cuadrícula de 3×2, y `Ctrl+Shift+F11` recorre las seis.
- **Mostrar solo mientras juego:** el overlay se oculta solo, y no gasta nada, cuando ningún juego está dibujando.

**FPS**
- **Los juegos Vulkan y OpenGL ya muestran FPS** (Minecraft con Vulkan, emuladores, DXVK…). Se leen de los eventos de presentación del kernel gráfico de Windows, sin tocar el juego, así que es seguro con anti-cheats.

**Compatibilidad de hardware**
- **Núcleos del CPU reconocidos de forma genérica:** Intel clásico, **Intel híbrido (núcleos P / E, p. ej. i9-13900KF)** y AMD Ryzen. Las cargas por hilo se agrupan en núcleos físicos; los híbridos muestran "Núcleo P1… E16". La lista de núcleos tiene su propio scroll para CPUs de 24 núcleos.
- **Respaldos que vienen con Windows**, sin driver ni admin; solo se usan cuando la librería de sensores no da el valor:
  - **reloj real del CPU** desde los contadores de Windows (la misma fuente que el Administrador de tareas);
  - **temperatura, ventilador y potencia en % de la GPU**, de cualquier marca.
- Respaldos genéricos de reloj, potencia y voltaje del CPU cuando usa nombres de sensores que HwPulse no conoce.

**Panel web**
- Botón **Mantener la pantalla encendida** (icono de sol), activo por defecto. Usa la API Wake Lock en `localhost`/`https`. En el teléfono, al abrir la dirección de la red local, usa un video mudo diminuto que evita que se apague la pantalla (funciona en la mayoría de Android; en iPhone por http puede apagarse igual).
- Ya no aparece un recuadro al tocar los botones en el teléfono.

**Interfaz**
- Los sliders se pueden arrastrar desde cualquier punto de la barra, no solo desde el círculo, en toda la app.
- Ajustes vuelve a caber en la ventana sin scroll.

**Diagnósticos para soporte** (línea de comandos)
- `--fps-probe <archivo>`: eventos de FPS por proceso y fuente.
- `--native-probe <archivo>`: lo que devuelven los respaldos de Windows.
- `--selftest-cpu <archivo>`: cómo se reconocen los núcleos del CPU.

## [0.1.6] - 2026-11-27
- **AMD Ryzen support:** temperature, clock and power now show (Ryzen sensor names were not mapped before). Per-thread loads are paired into physical cores.
- **NVIDIA GPU power** is read directly from the driver (NVML) when the sensor library reports none.
- The Power card says **"Total (CPU only)"** when the GPU doesn't report power.
- Better diagnostics in `errors.log` and `--dump`, and a targeted PawnIO update when the CPU reports no data.

## [0.1.5] - 2026-11-27
- The PawnIO driver check now tests that the driver actually works, not just that it's registered. A stopped driver is started; a broken one is reinstalled.
- A one-time "CPU diagnostics" line in `errors.log` when CPU temperature is missing.

## [0.1.4] - 2026-11-27
- **PawnIO is installed automatically** when missing. It's needed for CPU temperature and clock. The official installer is pinned to v2.2.0 and its checksum is verified, with an in-window notice.

## [0.1.3] - 2026-11-27
- **In-window update notice** with Update now / What's new / dismiss.

## [0.1.2] - 2026-11-27
- Close dialog centered on the main window.
- Stability: non-blocking "Start with Windows", web streaming that a slow client can't stall, GPU counters that retry, and cleanup of disconnected hardware.

## [0.1.1] - 2026-11-26
- Auto-update from GitHub Releases, minimize to tray, close dialog (tray or exit), start with Windows, FPS sliding window, and a new icon.

## [0.1.0] - 2026-11-25
- First release: window, overlay, web dashboard and tray.
