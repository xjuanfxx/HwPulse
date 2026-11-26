namespace HwPulse;

/// <summary>Ajustes de comportamiento de la ventana: qué hace la X e iniciar con Windows.</summary>
public sealed partial class MainViewModel
{
    public bool CloseAsk { get => _settings.CloseAction == "ask"; set { if (value) SetCloseAction("ask"); } }
    public bool CloseTray { get => _settings.CloseAction == "tray"; set { if (value) SetCloseAction("tray"); } }
    public bool CloseExit { get => _settings.CloseAction == "exit"; set { if (value) SetCloseAction("exit"); } }

    public void SetCloseAction(string action)
    {
        if (_settings.CloseAction == action) return;
        _settings.CloseAction = action;
        Raise(nameof(CloseAsk)); Raise(nameof(CloseTray)); Raise(nameof(CloseExit));
        SettingsDirty?.Invoke();
    }

    // El estado real vive en el Programador de tareas, no en settings.json. schtasks.exe puede tardar
    // segundos (antivirus lento), así que nunca se llama en el hilo de la interfaz: la consulta inicial
    // se lanza en segundo plano y el interruptor queda deshabilitado mientras algo está en curso.
    private bool _autostart, _autostartBusy, _autostartQueried;
    public bool StartWithWindows
    {
        get
        {
            if (!_autostartQueried) { _autostartQueried = true; _ = RefreshAutostartAsync(); }
            return _autostart;
        }
        set
        {
            if (value == _autostart || _autostartBusy) return;
            _ = SetAutostartAsync(value);
        }
    }
    public bool StartWithWindowsEnabled => !_autostartBusy;

    private async Task RefreshAutostartAsync()
    {
        SetAutostartBusy(true);
        _autostart = await Task.Run(Autostart.IsEnabled);
        Raise(nameof(StartWithWindows));
        SetAutostartBusy(false);
    }

    private async Task SetAutostartAsync(bool on)
    {
        _autostart = on; // optimista: el interruptor responde al instante
        Raise(nameof(StartWithWindows));
        SetAutostartBusy(true);
        // Si falla (sin administrador), vuelve a su estado real.
        bool ok = await Task.Run(() => Autostart.Set(on));
        if (!ok) _autostart = await Task.Run(Autostart.IsEnabled);
        Raise(nameof(StartWithWindows));
        SetAutostartBusy(false);
    }

    private void SetAutostartBusy(bool busy) { _autostartBusy = busy; Raise(nameof(StartWithWindowsEnabled)); }

    // ------------------------------------------------------------------ actualizaciones
    private string _updateKey = "UpdateCheck";
    private string? _updateVersion;
    private bool _updateBusy;

    /// <summary>Texto del enlace bajo la versión: buscar, al día, actualizar a X, descargando…</summary>
    public string UpdateText => _updateVersion == null ? Loc.T(_updateKey) : Loc.F(_updateKey, _updateVersion);
    public bool UpdateReady => _updateKey == "UpdateAvailable";
    public bool UpdateEnabled => !_updateBusy;

    public void SetUpdateState(string key, string? version = null, bool busy = false)
    {
        _updateKey = key; _updateVersion = version; _updateBusy = busy;
        Raise(nameof(UpdateText)); Raise(nameof(UpdateReady)); Raise(nameof(UpdateEnabled));
    }
}
