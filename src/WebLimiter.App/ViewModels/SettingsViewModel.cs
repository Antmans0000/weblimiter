using System;
using System.Threading.Tasks;
using System.Windows.Input;
using WebLimiter.App.Util;
using WebLimiter.Core.Backend;
using WebLimiter.Core.Storage;

namespace WebLimiter.App.ViewModels;

/// <summary>Switches the engine between the UI process and the WebLimiter Windows service.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SwitchableBackend _backend;
    private readonly Action _refresh;

    public SettingsViewModel(SwitchableBackend backend, Action refresh)
    {
        _backend = backend;
        _refresh = refresh;
        InstallServiceCommand = new RelayCommand(_ => Run(InstallService), _ => !_busy);
        UninstallServiceCommand = new RelayCommand(_ => Run(UninstallService), _ => !_busy);
        StartServiceCommand = new RelayCommand(_ => Run(StartService), _ => !_busy);
        UpdateCommand = new RelayCommand(_ => Run(Update, update: true), _ => !_busy);
    }

    public ICommand InstallServiceCommand { get; }
    public ICommand UninstallServiceCommand { get; }
    public ICommand StartServiceCommand { get; }
    public ICommand UpdateCommand { get; }

    public string VersionText => "Version installée : " + Updater.CurrentVersion.ToString(3);

    public string DataFolder => DataPaths.Root;

    private bool _busy, _installed, _running, _isError;
    private string? _message;

    public bool Busy { get => _busy; private set { Set(ref _busy, value); CommandManager.InvalidateRequerySuggested(); } }
    public bool ServiceInstalled { get => _installed; private set => Set(ref _installed, value); }
    public bool ServiceRunning { get => _running; private set => Set(ref _running, value); }
    public bool ServiceStopped => _installed && !_running;
    public string? Message { get => _message; private set => Set(ref _message, value); }

    private string? _updateMessage;
    /// <summary>Result of the last update check, shown in the Updates card.</summary>
    public string? UpdateMessage { get => _updateMessage; private set => Set(ref _updateMessage, value); }
    public bool IsError { get => _isError; private set => Set(ref _isError, value); }

    public bool IsServiceMode => _backend.Current is PipeBackend;

    public string ModeText => IsServiceMode
        ? "Le moteur tourne dans le service Windows WebLimiter."
        : "Le moteur tourne dans cette application.";

    public string ServiceStateText => !_installed
        ? "Service non installé : fermer complètement WebLimiter arrête le filtrage, les limites et les quotas."
        : _running
            ? "Service installé et démarré (démarrage automatique avec Windows, compte Système)."
            : "Service installé mais arrêté.";

    // ---- update downloads (Delivery Optimization) ----------------------------------------

    private bool _updateLimitEnabled;
    private int _updateLimitKb = 200;
    private string? _updateLimitMessage;

    public bool UpdateLimitEnabled
    {
        get => _updateLimitEnabled;
        set { if (Set(ref _updateLimitEnabled, value)) ApplyUpdateLimit(); }
    }

    public int UpdateLimitKb
    {
        get => _updateLimitKb;
        set { if (Set(ref _updateLimitKb, Math.Clamp(value, 1, 1_000_000)) && _updateLimitEnabled) ApplyUpdateLimit(); }
    }

    public string? UpdateLimitMessage { get => _updateLimitMessage; private set => Set(ref _updateLimitMessage, value); }

    private void ApplyUpdateLimit()
    {
        try
        {
            DeliveryOptimization.SetLimitKb(_updateLimitEnabled ? _updateLimitKb : null);
            UpdateLimitMessage = _updateLimitEnabled
                ? $"Téléchargements des mises à jour limités à {_updateLimitKb} Ko/s."
                : "Plus de limite sur les téléchargements des mises à jour.";
        }
        catch (Exception ex)
        {
            UpdateLimitMessage = "Impossible d'écrire la stratégie : " + ex.Message;
            DataPaths.LogError("Limite de l'Optimisation de livraison", ex);
        }
    }

    private void LoadUpdateLimit()
    {
        try
        {
            int? kb = DeliveryOptimization.GetLimitKb();
            _updateLimitEnabled = kb != null;
            if (kb != null) _updateLimitKb = kb.Value;
        }
        catch { _updateLimitEnabled = false; }
        Raise(nameof(UpdateLimitEnabled));
        Raise(nameof(UpdateLimitKb));
    }

    public void Refresh()
    {
        LoadUpdateLimit();
        var info = ServiceInstaller.Query();
        ServiceInstalled = info != null;
        ServiceRunning = info?.IsRunning == true;
        Raise(nameof(ServiceStopped));
        Raise(nameof(IsServiceMode));
        Raise(nameof(ModeText));
        Raise(nameof(ServiceStateText));
    }

    private async void Run(Func<string> action, bool update = false)
    {
        void Show(string? text)
        {
            if (update) UpdateMessage = text;
            else Message = text;
        }

        Busy = true;
        Show(update ? "Recherche de la dernière version…" : "Opération en cours…");
        IsError = false;
        try
        {
            Show(await Task.Run(action));
        }
        catch (Exception ex)
        {
            DataPaths.LogError(update ? "Mise à jour" : "Gestion du service", ex);
            Show(update ? "Mise à jour impossible : " + ex.Message : ex.Message);
            IsError = true;
        }
        finally
        {
            Busy = false;
            Refresh();
            _refresh();
        }
    }

    private string InstallService()
    {
        // The in-process engine must release WinDivert and flush its files before the service loads them.
        _backend.Current.Dispose();
        try
        {
            ServiceInstaller.Install();
            ConnectToService();
            return "Service installé : le filtrage continue même interface fermée.";
        }
        catch
        {
            FallBackToLocal();
            throw;
        }
    }

    private string StartService()
    {
        ServiceInstaller.Start();
        ConnectToService();
        return "Service démarré.";
    }

    private string UninstallService()
    {
        ServiceInstaller.Uninstall();
        FallBackToLocal();
        return "Service désinstallé : le moteur tourne de nouveau dans l'application.";
    }

    /// <summary>Downloads the setup of the latest GitHub release if it is newer, and launches it.</summary>
    private string Update()
    {
        var release = Updater.GetLatest();
        if (release.Version <= Updater.CurrentVersion)
            return $"WebLimiter est à jour (dernière version publiée : {release.Version.ToString(3)}).";
        string setup = Updater.Download(release);
        Updater.Launch(setup);
        return $"Installation de la version {release.Version.ToString(3)} lancée : WebLimiter va se fermer.";
    }

    private void ConnectToService()
    {
        var pipe = new PipeBackend();
        if (!pipe.WaitUntilReady(TimeSpan.FromSeconds(10)))
        {
            pipe.Dispose();
            throw new InvalidOperationException("Le service a démarré mais ne répond pas sur son canal de communication.");
        }
        _backend.Swap(pipe);
    }

    private void FallBackToLocal()
    {
        var local = new LocalBackend();
        local.SetFiltering(true);
        _backend.Swap(local);
    }
}
