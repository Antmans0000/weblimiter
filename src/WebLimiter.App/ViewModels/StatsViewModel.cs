using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using WebLimiter.App.Util;
using WebLimiter.Core.Backend;
using WebLimiter.Core.Engine;
using WebLimiter.Core.Storage;

namespace WebLimiter.App.ViewModels;

public enum StatsPeriod { Last24Hours, Today, Yesterday, Last7Days, Last14Days, Last30Days, ThisMonth, LastMonth, Last12Months, Custom }

public enum StatsScale { Auto, Hour, Day, Month }

public enum StatsDirection { Both, Down, Up }

public sealed record StatsRow(string Path, string Name, ImageSource? Icon, long Down, long Up, double Fraction,
    long PeakDown = 0, long PeakUp = 0, double PeakDownFraction = 0, double PeakUpFraction = 0, bool IsSelected = false)
{
    public long Total => Down + Up;
    public string DownText => Format.Bytes(Down);
    public string UpText => Format.Bytes(Up);
    public string TotalText => Format.Bytes(Total);
    public double BarWidth => Math.Max(2, Fraction * 160);

    public long PeakMax => Math.Max(PeakDown, PeakUp);
    public string PeakDownText => Format.Rate(PeakDown);
    public string PeakUpText => Format.Rate(PeakUp);
    public double PeakDownBarWidth => PeakDown > 0 ? Math.Max(2, PeakDownFraction * 140) : 0;
    public double PeakUpBarWidth => PeakUp > 0 ? Math.Max(2, PeakUpFraction * 140) : 0;
}

/// <summary>One bar of the chart: a bucket (hour, day or month) starting at <see cref="Day"/>.</summary>
public sealed record DayBar(DateTime Day, long Down, long Up);

public sealed class StatsViewModel : ObservableObject
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly string[] DateFormats =
    {
        "d/M/yyyy H:mm", "d/M/yyyy H'h'mm", "d/M/yyyy H'h'", "d/M/yyyy H", "d/M/yyyy",
        "d/M H:mm", "d/M H'h'mm", "d/M H'h'", "d/M H", "d/M",
    };

    private readonly IBackend _backend;
    private readonly HashSet<string> _selection = new(StringComparer.OrdinalIgnoreCase);
    private StatsResult _result = new();
    private bool _refreshing;

    public StatsViewModel(IBackend backend)
    {
        _backend = backend;
        var today = DateTime.Today;
        _fromText = today.ToString("dd/MM/yyyy HH:mm", Fr);
        _toText = today.AddDays(1).ToString("dd/MM/yyyy HH:mm", Fr);
    }

    // ---- filters ------------------------------------------------------------------------

    private StatsPeriod _period = StatsPeriod.Today;
    public StatsPeriod Period
    {
        get => _period;
        set
        {
            if (!Set(ref _period, value)) return;
            Raise(nameof(IsCustom));
            if (value != StatsPeriod.Custom)
            {
                // Pre-fill the custom range with the chosen preset so it can be adjusted from there.
                var (from, to) = PresetRange(value);
                _fromText = from.ToString("dd/MM/yyyy HH:mm", Fr);
                _toText = to.ToString("dd/MM/yyyy HH:mm", Fr);
                Raise(nameof(FromText));
                Raise(nameof(ToText));
            }
            Refresh();
        }
    }

    public bool IsCustom => _period == StatsPeriod.Custom;

    private string _fromText, _toText;
    public string FromText { get => _fromText; set { if (Set(ref _fromText, value)) ApplyCustom(); } }
    public string ToText { get => _toText; set { if (Set(ref _toText, value)) ApplyCustom(); } }

    private string? _rangeError;
    public string? RangeError { get => _rangeError; private set => Set(ref _rangeError, value); }

    private StatsScale _scale = StatsScale.Auto;
    public StatsScale Scale { get => _scale; set { if (Set(ref _scale, value)) Refresh(); } }

    private StatsDirection _direction = StatsDirection.Both;
    public StatsDirection Direction { get => _direction; set { if (Set(ref _direction, value)) BuildChart(); } }

    private string _search = "";
    public string Search { get => _search; set { if (Set(ref _search, value)) Refresh(); } }

    private bool _hideSystem;
    public bool HideSystem { get => _hideSystem; set { if (Set(ref _hideSystem, value)) Refresh(); } }

    // ---- results ------------------------------------------------------------------------

    private IReadOnlyList<StatsRow> _rows = Array.Empty<StatsRow>();
    public IReadOnlyList<StatsRow> Rows { get => _rows; private set => Set(ref _rows, value); }

    private IReadOnlyList<DayBar> _days = Array.Empty<DayBar>();
    public IReadOnlyList<DayBar> Days { get => _days; private set => Set(ref _days, value); }

    private StatsGranularity _granularity = StatsGranularity.Day;
    public StatsGranularity Granularity { get => _granularity; private set => Set(ref _granularity, value); }

    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    private string _rangeText = "";
    public string RangeText { get => _rangeText; private set => Set(ref _rangeText, value); }

    private string _chartScope = "";
    /// <summary>Which apps the chart and the totals cover.</summary>
    public string ChartScope { get => _chartScope; private set => Set(ref _chartScope, value); }

    public bool HasSelection => _selection.Count > 0;

    private string? _note;
    public string? Note { get => _note; private set => Set(ref _note, value); }

    /// <summary>Apps ordered by their highest rate (down or up) over the period.</summary>
    private IReadOnlyList<StatsRow> _peakRows = Array.Empty<StatsRow>();
    public IReadOnlyList<StatsRow> PeakRows { get => _peakRows; private set => Set(ref _peakRows, value); }

    private string _peakSummary = "";
    public string PeakSummary { get => _peakSummary; private set => Set(ref _peakSummary, value); }

    // ---- range --------------------------------------------------------------------------

    private static (DateTime From, DateTime To) PresetRange(StatsPeriod period)
    {
        var now = DateTime.Now;
        var today = DateTime.Today;
        var month = new DateTime(today.Year, today.Month, 1);
        var nextHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0).AddHours(1);
        return period switch
        {
            StatsPeriod.Last24Hours => (nextHour.AddHours(-24), nextHour),
            StatsPeriod.Yesterday => (today.AddDays(-1), today),
            StatsPeriod.Last7Days => (today.AddDays(-6), today.AddDays(1)),
            StatsPeriod.Last14Days => (today.AddDays(-13), today.AddDays(1)),
            StatsPeriod.Last30Days => (today.AddDays(-29), today.AddDays(1)),
            StatsPeriod.ThisMonth => (month, today.AddDays(1)),
            StatsPeriod.LastMonth => (month.AddMonths(-1), month),
            StatsPeriod.Last12Months => (month.AddMonths(-11), today.AddDays(1)),
            _ => (today, today.AddDays(1)),
        };
    }

    /// <summary>Parses "13/11/2026 08:00", "13/11 8h", "13/11/2026"… A date alone in "to" means the end of that day.</summary>
    private static DateTime? ParseDate(string text, bool isEnd)
    {
        text = text.Trim();
        if (!DateTime.TryParseExact(text, DateFormats, Fr, DateTimeStyles.AllowWhiteSpaces, out var d)) return null;
        bool hasTime = text.Contains(' ') || text.Contains('h');
        d = new DateTime(d.Year, d.Month, d.Day, d.Hour, 0, 0);
        return isEnd && !hasTime ? d.AddDays(1) : d;
    }

    private (DateTime From, DateTime To)? Range()
    {
        if (_period != StatsPeriod.Custom) { RangeError = null; return PresetRange(_period); }
        var from = ParseDate(_fromText, false);
        var to = ParseDate(_toText, true);
        if (from == null || to == null) { RangeError = "Date invalide — format : 13/11/2026 08:00"; return null; }
        if (to <= from) { RangeError = "La fin doit être après le début"; return null; }
        RangeError = null;
        return (from.Value, to.Value);
    }

    private void ApplyCustom()
    {
        if (_period != StatsPeriod.Custom) { _period = StatsPeriod.Custom; Raise(nameof(Period)); Raise(nameof(IsCustom)); }
        Refresh();
    }

    private StatsGranularity EffectiveGranularity(DateTime from, DateTime to)
    {
        double days = (to - from).TotalDays;
        var g = _scale switch
        {
            StatsScale.Hour => StatsGranularity.Hour,
            StatsScale.Day => StatsGranularity.Day,
            StatsScale.Month => StatsGranularity.Month,
            _ => days <= 3 ? StatsGranularity.Hour : days <= 93 ? StatsGranularity.Day : StatsGranularity.Month,
        };
        // Beyond a month, hourly bars are too thin to read.
        return g == StatsGranularity.Hour && days > 31 ? StatsGranularity.Day : g;
    }

    /// <summary>Shows one bucket in detail: a day by hour, a month by day.</summary>
    public void DrillDown(DateTime start)
    {
        if (_granularity == StatsGranularity.Hour) return;
        var end = _granularity == StatsGranularity.Month ? start.AddMonths(1) : start.AddDays(1);
        _scale = StatsScale.Auto;
        Raise(nameof(Scale));
        _fromText = start.ToString("dd/MM/yyyy HH:mm", Fr);
        _toText = end.ToString("dd/MM/yyyy HH:mm", Fr);
        Raise(nameof(FromText));
        Raise(nameof(ToText));
        _period = StatsPeriod.Custom;
        Raise(nameof(Period));
        Raise(nameof(IsCustom));
        Refresh();
    }

    // ---- selection ----------------------------------------------------------------------

    public void ToggleSelection(string path)
    {
        if (!_selection.Remove(path)) _selection.Add(path);
        Raise(nameof(HasSelection));
        Refresh();
    }

    public void ClearSelection()
    {
        _selection.Clear();
        Raise(nameof(HasSelection));
        Refresh();
    }

    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static bool IsSystem(string path) =>
        !AppNames.IsExecutable(path) || path.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase);

    private bool Visible(AppUsage a)
    {
        if (_hideSystem && IsSystem(a.AppPath)) return false;
        if (string.IsNullOrWhiteSpace(_search)) return true;
        return AppNames.Display(a.AppPath).Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase)
               || a.AppPath.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private bool FilterActive => _hideSystem || !string.IsNullOrWhiteSpace(_search);

    // ---- refresh ------------------------------------------------------------------------

    public void Refresh()
    {
        if (_refreshing) return;
        var range = Range();
        if (range == null) return;
        var (from, to) = range.Value;
        var granularity = EffectiveGranularity(from, to);
        _refreshing = true;
        try
        {
            var query = new StatsQuery { From = from, To = to, Granularity = granularity };
            StatsResult result;
            try
            {
                result = _backend.QueryStats(query);
                // Without an explicit selection, the chart follows the search and the system filter.
                List<string>? scope = _selection.Count > 0 ? _selection.ToList()
                    : FilterActive ? result.Apps.Where(Visible).Select(a => a.AppPath).ToList()
                    : null;
                if (scope != null)
                {
                    query.Apps = scope.Count > 0 ? scope : new List<string> { "\0" };
                    result = _backend.QueryStats(query);
                }
            }
            catch { return; }

            _result = result;
            Granularity = granularity;
            RangeText = DescribeRange(from, to);
            BuildRows();
            BuildChart();
        }
        finally { _refreshing = false; }
    }

    private void BuildRows()
    {
        var visible = _result.Apps.Where(Visible).ToList();
        long max = visible.Count > 0 ? Math.Max(1, visible.Max(a => a.Total)) : 1;
        Rows = visible.Select(a => new StatsRow(a.AppPath, AppNames.Display(a.AppPath), IconCache.Get(a.AppPath),
            a.Down, a.Up, a.Total / (double)max, a.PeakDown, a.PeakUp, 0, 0, _selection.Contains(a.AppPath))).ToList();

        var sel = _result.Selected;
        int count = _selection.Count > 0 ? _selection.Count : visible.Count;
        ChartScope = _selection.Count > 0 ? $"Graphe et totaux : {_selection.Count} application(s) sélectionnée(s)"
            : FilterActive ? $"Graphe et totaux : {visible.Count} application(s) filtrée(s)"
            : "Graphe et totaux : toutes les applications — cochez des lignes pour n'afficher qu'elles";
        Summary = $"↓ {Format.Bytes(sel.Down)}    ↑ {Format.Bytes(sel.Up)}    Total {Format.Bytes(sel.Total)}    —    {count} applications";

        double maxDown = Math.Max(1, visible.Select(a => a.PeakDown).DefaultIfEmpty().Max());
        double maxUp = Math.Max(1, visible.Select(a => a.PeakUp).DefaultIfEmpty().Max());
        PeakRows = visible.Where(a => a.PeakDown > 0 || a.PeakUp > 0)
            .Select(a => new StatsRow(a.AppPath, AppNames.Display(a.AppPath), IconCache.Get(a.AppPath), a.Down, a.Up, 0,
                a.PeakDown, a.PeakUp, a.PeakDown / maxDown, a.PeakUp / maxUp))
            .OrderByDescending(r => r.PeakMax).ToList();
        PeakSummary = $"Débit le plus élevé :    ↓ {Format.Rate(sel.PeakDown)}    ↑ {Format.Rate(sel.PeakUp)}";

        Note = _result.Approximate
            ? (_result.HourlySince is { } since
                ? $"Le détail par heure existe depuis le {since:dd/MM/yyyy} : avant, seuls les totaux par jour sont connus (comptés en entier, placés en début de journée)."
                : "Le détail par heure commence à s'enregistrer maintenant : les jours précédents n'ont que des totaux par jour.")
            : null;
    }

    private void BuildChart()
    {
        Days = _result.Series.Select(b => new DayBar(b.Start,
            _direction == StatsDirection.Up ? 0 : b.Down,
            _direction == StatsDirection.Down ? 0 : b.Up)).ToList();
    }

    private static string DescribeRange(DateTime from, DateTime to)
    {
        var last = to.AddTicks(-1);
        bool wholeDays = from.TimeOfDay == TimeSpan.Zero && to.TimeOfDay == TimeSpan.Zero;
        if (wholeDays)
            return from.Date == last.Date
                ? from.ToString("dddd d MMMM yyyy", Fr)
                : $"du {from.ToString("d MMMM yyyy", Fr)} au {last.ToString("d MMMM yyyy", Fr)}";
        return $"du {from.ToString("dd/MM/yyyy HH:mm", Fr)} au {to.ToString("dd/MM/yyyy HH:mm", Fr)}";
    }

    // ---- export / clear -----------------------------------------------------------------

    /// <summary>Writes the chart buckets and the per-app table as CSV (semicolons, UTF-8 with BOM, for Excel).</summary>
    public void ExportCsv(string path)
    {
        var sb = new StringBuilder();
        string Cell(string s) => s.Contains(';') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        string fmt = _granularity switch
        {
            StatsGranularity.Hour => "yyyy-MM-dd HH:mm",
            StatsGranularity.Month => "yyyy-MM",
            _ => "yyyy-MM-dd",
        };

        sb.AppendLine($"Période;{Cell(RangeText)}");
        sb.AppendLine($"Portée;{Cell(ChartScope)}");
        sb.AppendLine();
        sb.AppendLine("Début;Reçu (octets);Envoyé (octets);Total (octets)");
        foreach (var b in _result.Series)
            sb.AppendLine($"{b.Start.ToString(fmt, CultureInfo.InvariantCulture)};{b.Down};{b.Up};{b.Down + b.Up}");
        sb.AppendLine();
        sb.AppendLine("Application;Chemin;Reçu (octets);Envoyé (octets);Total (octets);Pic reçu (o/s);Pic envoyé (o/s)");
        foreach (var r in _rows)
            sb.AppendLine($"{Cell(r.Name)};{Cell(r.Path)};{r.Down};{r.Up};{r.Total};{r.PeakDown};{r.PeakUp}");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    public string SuggestedFileName =>
        $"weblimiter-stats-{DateTime.Now:yyyyMMdd-HHmm}.csv";

    public void Clear()
    {
        try { _backend.ClearStats(); }
        catch { /* service unreachable: nothing cleared */ }
        Refresh();
    }
}
