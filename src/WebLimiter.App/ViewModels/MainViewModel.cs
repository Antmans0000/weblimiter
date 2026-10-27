using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;
using WebLimiter.App.Util;
using WebLimiter.Core.Backend;
using WebLimiter.Core.Engine;
using WebLimiter.Core.Storage;

namespace WebLimiter.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly Dictionary<string, AppRowViewModel> _rowsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ConnectionRow> _connByKey = new();

    public MainViewModel(SwitchableBackend backend)
    {
        Backend = backend;
        Settings = new SettingsViewModel(backend, Refresh);
        Rules = new RuleBook(backend);
        Stats = new StatsViewModel(backend);
        Exclusions = new ExclusionsViewModel(backend, Rules);

        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Apps);
        view.Filter = o => Filter((AppRowViewModel)o);
        view.SortDescriptions.Add(new SortDescription(nameof(AppRowViewModel.TotalRate), ListSortDirection.Descending));
        view.SortDescriptions.Add(new SortDescription(nameof(AppRowViewModel.Name), ListSortDirection.Ascending));
        view.IsLiveSorting = true;
        view.IsLiveFiltering = true;
        foreach (var p in new[] { nameof(AppRowViewModel.TotalRate), nameof(AppRowViewModel.DownRate),
                     nameof(AppRowViewModel.UpRate), nameof(AppRowViewModel.TodayDown), nameof(AppRowViewModel.TodayUp),
                     nameof(AppRowViewModel.Connections) })
            view.LiveSortingProperties.Add(p);
        view.LiveFilteringProperties.Add(nameof(AppRowViewModel.IsActive));
        view.LiveFilteringProperties.Add(nameof(AppRowViewModel.HasRule));
        AppsView = view;

        Rules.Changed += () =>
        {
            foreach (var rule in Rules.All) RowFor(rule.AppPath, AppNames.Display(rule.AppPath));
            foreach (var row in Apps) row.SyncRule();
        };
        Rules.Reload();

        ToggleEngineCommand = new RelayCommand(_ => ToggleEngine());
    }

    public IBackend Backend { get; }
    public RuleBook Rules { get; }
    public StatsViewModel Stats { get; }
    public ExclusionsViewModel Exclusions { get; }
    public SettingsViewModel Settings { get; }
    public ObservableCollection<AppRowViewModel> Apps { get; } = new();
    public ListCollectionView AppsView { get; }
    public ObservableCollection<ConnectionRow> Connections { get; } = new();
    public History TotalHistory { get; } = new();
    public ICommand ToggleEngineCommand { get; }

    /// <summary>Raised after each refresh so the window can repaint the graph.</summary>
    public event Action? Refreshed;

    private string _search = "";
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) AppsView.Refresh(); }
    }

    private bool _showInactive;
    public bool ShowInactive
    {
        get => _showInactive;
        set { if (Set(ref _showInactive, value)) AppsView.Refresh(); }
    }

    private AppRowViewModel? _selected;
    public AppRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Connections.Clear();
            _connByKey.Clear();
            RefreshConnections();
            Raise(nameof(GraphHistory));
            Raise(nameof(GraphTitle));
        }
    }

    public History GraphHistory => _selected?.History ?? TotalHistory;
    public string GraphTitle => _selected?.Name ?? "Trafic total";

    private string _totalDownText = "", _totalUpText = "", _status = "";
    public string TotalDownText { get => _totalDownText; private set => Set(ref _totalDownText, value); }
    public string TotalUpText { get => _totalUpText; private set => Set(ref _totalUpText, value); }
    public string Status { get => _status; set => Set(ref _status, value); }

    private BackendStatus _backendStatus = new(false, false, null, false);
    public bool IsRunning => _backendStatus.IsRunning;
    public bool IsServiceMode => _backendStatus.IsService;
    public string? EngineError => !_backendStatus.Connected
        ? "Le service WebLimiter ne répond pas."
        : _backendStatus.Error;

    private bool Filter(AppRowViewModel row)
    {
        if (!_showInactive && !row.IsActive && !row.HasRule) return false;
        if (string.IsNullOrWhiteSpace(_search)) return true;
        return row.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
               || row.Path.Contains(_search, StringComparison.OrdinalIgnoreCase);
    }

    private AppRowViewModel RowFor(string path, string name)
    {
        if (_rowsByPath.TryGetValue(path, out var row)) return row;
        row = new AppRowViewModel(path, name, Rules);
        _rowsByPath[path] = row;
        Apps.Add(row);
        return row;
    }

    /// <summary>Pulls the latest state from the backend; called once per second on the UI thread.</summary>
    public void Refresh()
    {
        EngineSnapshot snap;
        Dictionary<string, AppUsage> today;
        try
        {
            _backendStatus = Backend.GetStatus();
            snap = Backend.GetSnapshot();
            today = Backend.GetUsageByApp(DateTime.Today, DateTime.Today)
                .ToDictionary(u => u.AppPath, StringComparer.OrdinalIgnoreCase);
            Rules.Reload();
        }
        catch
        {
            _backendStatus = new BackendStatus(false, false, null, _backendStatus.IsService);
            snap = EngineSnapshot.Empty;
            today = new(StringComparer.OrdinalIgnoreCase);
        }

        var byPath = new Dictionary<string, AppSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in snap.Apps)
        {
            byPath[a.Path] = a;
            RowFor(a.Path, a.Name);
        }
        foreach (var row in Apps)
            row.Update(byPath.GetValueOrDefault(row.Path), today.GetValueOrDefault(row.Path));

        TotalHistory.Push(snap.TotalDownRate, snap.TotalUpRate);
        TotalDownText = Format.Rate(snap.TotalDownRate) is { Length: > 0 } d ? d : "0 o/s";
        TotalUpText = Format.Rate(snap.TotalUpRate) is { Length: > 0 } u ? u : "0 o/s";
        string where = _backendStatus.IsService ? "service Windows" : "moteur intégré";
        Status = IsRunning
            ? $"Filtrage actif ({where}) — {snap.Apps.Count(a => a.Connections > 0)} applications connectées"
            : EngineError ?? $"Filtrage suspendu ({where}) — le trafic n'est ni mesuré ni limité";
        Raise(nameof(IsRunning));
        Raise(nameof(IsServiceMode));
        Raise(nameof(EngineError));

        RefreshConnections();
        Refreshed?.Invoke();
    }

    private void RefreshConnections()
    {
        if (_selected == null) return;
        IReadOnlyList<ConnectionSnapshot> live;
        try { live = Backend.GetConnections(_selected.Path); }
        catch { return; }
        var seen = new HashSet<string>();
        foreach (var c in live)
        {
            string key = ConnectionRow.MakeKey(c);
            seen.Add(key);
            if (_connByKey.TryGetValue(key, out var row)) row.Update(c);
            else
            {
                row = new ConnectionRow(c);
                _connByKey[key] = row;
                Connections.Add(row);
            }
        }
        for (int i = Connections.Count - 1; i >= 0; i--)
        {
            if (seen.Contains(Connections[i].Key)) continue;
            _connByKey.Remove(Connections[i].Key);
            Connections.RemoveAt(i);
        }
    }

    private void ToggleEngine()
    {
        try { Backend.SetFiltering(!IsRunning); }
        catch (Exception ex) { DataPaths.LogError("Changement d'état du filtrage impossible", ex); }
        Refresh();
    }
}
