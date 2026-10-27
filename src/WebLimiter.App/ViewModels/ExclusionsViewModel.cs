using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using WebLimiter.App.Util;
using WebLimiter.Core.Backend;
using WebLimiter.Core.Engine;
using WebLimiter.Core.Storage;

namespace WebLimiter.App.ViewModels;

public enum ExclusionMode { Services, Applications }

/// <summary>A Windows service that can be cut off from the network.</summary>
public sealed class ServiceRow : ObservableObject
{
    private readonly RuleBook _rules;
    private ServiceInfo _info;

    public ServiceRow(ServiceInfo info, RuleBook rules)
    {
        _info = info;
        _rules = rules;
    }

    public string Name => _info.Name;
    public string DisplayName => _info.DisplayName;
    public string StateText => _info.StateText;
    public string StartText => _info.StartText;
    public bool IsRunning => _info.IsRunning;
    public string PidText => _info.Pid > 0 ? _info.Pid.ToString() : "";
    public string RuleKey => _info.RuleKey;

    /// <summary>Other services sharing the same svchost process (they are cut off together).</summary>
    public int SharedWith { get; private set; }

    public string HostText => !_info.IsSvchost
        ? Path.GetFileName(_info.ImagePath)
        : _info.Pid == 0 ? "svchost"
        : SharedWith > 0 ? $"svchost (partagé avec {SharedWith})" : "svchost (dédié)";

    public bool Blocked
    {
        get => _rules.Get(RuleKey)?.Blocked == true;
        set => _rules.Update(RuleKey, r => r.Blocked = value);
    }

    public void Update(ServiceInfo info, int sharedWith)
    {
        _info = info;
        SharedWith = sharedWith;
        Raise(string.Empty);
    }

    public void SyncRule() => Raise(nameof(Blocked));
}

/// <summary>An executable present on the machine (running, known from rules or statistics, or added by hand).</summary>
public sealed class ProgramRow : ObservableObject
{
    private readonly RuleBook _rules;
    private bool _isRunning;

    public ProgramRow(string path, RuleBook rules)
    {
        Path = path;
        _rules = rules;
        Name = AppNames.Display(path);
        Icon = IconCache.Get(path);
    }

    public string Path { get; }
    public string Name { get; }
    public ImageSource? Icon { get; }
    public bool IsRunning { get => _isRunning; set { if (Set(ref _isRunning, value)) Raise(nameof(StateText)); } }
    public string StateText => _isRunning ? "En cours" : "";

    public bool Blocked
    {
        get => _rules.Get(Path)?.Blocked == true;
        set => _rules.Update(Path, r => r.Blocked = value);
    }

    public void SyncRule() => Raise(nameof(Blocked));
}

public sealed class ExclusionsViewModel : ObservableObject
{
    private readonly IBackend _backend;
    private readonly RuleBook _rules;
    private readonly Dictionary<string, ServiceRow> _serviceRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProgramRow> _programRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _added = new(StringComparer.OrdinalIgnoreCase);

    public ExclusionsViewModel(IBackend backend, RuleBook rules)
    {
        _backend = backend;
        _rules = rules;

        ServicesView = new ListCollectionView(Services) { Filter = o => Match(((ServiceRow)o).DisplayName, ((ServiceRow)o).Name, ((ServiceRow)o).Blocked) };
        ServicesView.SortDescriptions.Add(new SortDescription(nameof(ServiceRow.DisplayName), ListSortDirection.Ascending));
        ProgramsView = new ListCollectionView(Programs) { Filter = o => Match(((ProgramRow)o).Name, ((ProgramRow)o).Path, ((ProgramRow)o).Blocked) };
        ProgramsView.SortDescriptions.Add(new SortDescription(nameof(ProgramRow.IsRunning), ListSortDirection.Descending));
        ProgramsView.SortDescriptions.Add(new SortDescription(nameof(ProgramRow.Name), ListSortDirection.Ascending));

        BlockUpdatesCommand = new RelayCommand(_ => SetWindowsUpdateBlocked(true));
        AllowUpdatesCommand = new RelayCommand(_ => SetWindowsUpdateBlocked(false));

        rules.Changed += () =>
        {
            foreach (var r in _serviceRows.Values) r.SyncRule();
            foreach (var r in _programRows.Values) r.SyncRule();
            RaiseUpdateState();
            if (_onlyBlocked) { ServicesView.Refresh(); ProgramsView.Refresh(); }
        };
    }

    public ObservableCollection<ServiceRow> WindowsUpdate { get; } = new();
    public ObservableCollection<ServiceRow> Services { get; } = new();
    public ObservableCollection<ProgramRow> Programs { get; } = new();
    public ListCollectionView ServicesView { get; }
    public ListCollectionView ProgramsView { get; }
    public ICommand BlockUpdatesCommand { get; }
    public ICommand AllowUpdatesCommand { get; }

    private ExclusionMode _mode;
    public ExclusionMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            Raise(nameof(ShowServices));
            Raise(nameof(ShowPrograms));
        }
    }

    public bool ShowServices => _mode == ExclusionMode.Services;
    public bool ShowPrograms => _mode == ExclusionMode.Applications;

    private string _search = "";
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) { ServicesView.Refresh(); ProgramsView.Refresh(); } }
    }

    private bool _onlyBlocked;
    public bool OnlyBlocked
    {
        get => _onlyBlocked;
        set { if (Set(ref _onlyBlocked, value)) { ServicesView.Refresh(); ProgramsView.Refresh(); } }
    }

    private bool Match(string name, string detail, bool blocked)
    {
        if (_onlyBlocked && !blocked) return false;
        if (string.IsNullOrWhiteSpace(_search)) return true;
        return name.Contains(_search, StringComparison.OrdinalIgnoreCase)
               || detail.Contains(_search, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Windows Update ---------------------------------------------------------------

    public bool UpdatesBlocked => WindowsUpdate.Count > 0 && WindowsUpdate.All(s => s.Blocked);
    public bool UpdatesPartlyBlocked => !UpdatesBlocked && WindowsUpdate.Any(s => s.Blocked);

    public string UpdatesStateText => UpdatesBlocked
        ? "Bloquées : les services de mise à jour n'ont plus accès à Internet."
        : UpdatesPartlyBlocked
            ? "Partiellement bloquées : certains services de mise à jour ont encore accès à Internet."
            : "Autorisées : Windows peut télécharger les mises à jour.";

    private void RaiseUpdateState()
    {
        Raise(nameof(UpdatesBlocked));
        Raise(nameof(UpdatesPartlyBlocked));
        Raise(nameof(UpdatesStateText));
    }

    private void SetWindowsUpdateBlocked(bool blocked)
    {
        foreach (var info in ServiceCatalog.GetWindowsUpdateServices())
            _rules.Update(info.RuleKey, r => r.Blocked = blocked);
    }

    // ---- refresh ----------------------------------------------------------------------

    public void AddProgram(string path)
    {
        _added.Add(path);
        Refresh();
        _rules.Update(path, r => r.Blocked = true);
    }

    public void Refresh()
    {
        var services = ServiceCatalog.GetAll(force: true);
        var perPid = services.Where(s => s.Pid > 0).GroupBy(s => s.Pid).ToDictionary(g => g.Key, g => g.Count());
        foreach (var info in services)
        {
            int shared = info.Pid > 0 && info.IsSvchost ? perPid[info.Pid] - 1 : 0;
            if (!_serviceRows.TryGetValue(info.Name, out var row))
            {
                row = new ServiceRow(info, _rules);
                _serviceRows[info.Name] = row;
                Services.Add(row);
            }
            row.Update(info, shared);
        }

        WindowsUpdate.Clear();
        foreach (var info in services.Where(s => s.IsWindowsUpdate))
            WindowsUpdate.Add(_serviceRows[info.Name]);
        RaiseUpdateState();

        RefreshPrograms();
    }

    private void RefreshPrograms()
    {
        var running = ProcessCatalog.RunningExecutables();
        // svchost is handled per service in the Services list.
        running.RemoveWhere(p => p.EndsWith("\\svchost.exe", StringComparison.OrdinalIgnoreCase));

        var known = new HashSet<string>(running, StringComparer.OrdinalIgnoreCase);
        known.UnionWith(_added);
        known.UnionWith(_rules.All.Select(r => r.AppPath));
        try { known.UnionWith(_backend.GetUsageByApp(null, DateTime.Today).Select(u => u.AppPath)); }
        catch { /* backend unreachable */ }

        foreach (string path in known.Where(AppNames.IsExecutable))
        {
            if (!_programRows.TryGetValue(path, out var row))
            {
                row = new ProgramRow(path, _rules);
                _programRows[path] = row;
                Programs.Add(row);
            }
            row.IsRunning = running.Contains(path);
        }
    }
}
