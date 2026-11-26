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

    // El estado real vive en el Programador de tareas, no en settings.json: se consulta una vez y se cachea.
    private bool? _autostart;
    public bool StartWithWindows
    {
        get => _autostart ??= Autostart.IsEnabled();
        set
        {
            if (value == StartWithWindows) return;
            // Si falla (sin administrador), el interruptor vuelve a su estado real.
            _autostart = Autostart.Set(value) ? value : Autostart.IsEnabled();
            Raise();
        }
    }

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
