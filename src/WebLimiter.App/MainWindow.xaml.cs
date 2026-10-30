using System;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WebLimiter.App.Util;
using WebLimiter.App.ViewModels;

namespace WebLimiter.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer;
    private readonly UiSettings _ui = UiSettings.Load();
    private bool _layoutReady;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        AppsGrid.Sorting += OnSorting;
        _vm = vm;
        DataContext = vm;
        vm.Refreshed += () => Graph.Invalidate();

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => vm.Refresh(), Dispatcher);
        _timer.Start();
        vm.Refresh();

        AutoStartBox.IsChecked = AutoStart.IsEnabled();

        ApplyLayout(_ui.ActivityLayout);
        (_ui.ActivityLayout switch
        {
            ActivityLayout.SideBySide => LayoutSideBySide,
            ActivityLayout.ListOnly => LayoutListOnly,
            ActivityLayout.Compact => LayoutCompact,
            _ => LayoutStandard,
        }).IsChecked = true;
        _layoutReady = true;
    }

    private void Layout_Checked(object sender, RoutedEventArgs e)
    {
        if (!_layoutReady || (sender as FrameworkElement)?.Tag is not string tag
            || !Enum.TryParse(tag, out ActivityLayout layout)) return;
        ApplyLayout(layout);
        _ui.ActivityLayout = layout;
        _ui.Save();
    }

    /// <summary>Indexes of the rule columns (limits, block, quota) hidden by the compact layout.</summary>
    private static readonly int[] RuleColumns = { 3, 4, 5, 6 };
    private GridLength[]? _headerWidths;

    /// <summary>Places the app list, the splitter and the details (graph + connections) for a layout.</summary>
    private void ApplyLayout(ActivityLayout layout)
    {
        var rows = ActivityGrid.RowDefinitions;
        var cols = ActivityGrid.ColumnDefinitions;
        bool side = layout == ActivityLayout.SideBySide;
        bool details = layout != ActivityLayout.ListOnly;

        // Rows: toolbar, list, splitter, details below. Columns: list, splitter, details on the right.
        rows[2].Height = new GridLength(details && !side ? 5 : 0);
        rows[3].Height = new GridLength(details && !side ? 250 : 0);
        rows[3].MinHeight = details && !side ? 120 : 0;
        cols[1].Width = new GridLength(side ? 5 : 0);
        cols[2].Width = new GridLength(side ? 540 : 0);
        cols[2].MinWidth = side ? 300 : 0;

        Grid.SetRow(ActivitySplitter, side ? 1 : 2);
        Grid.SetColumn(ActivitySplitter, side ? 1 : 0);
        ActivitySplitter.Visibility = details ? Visibility.Visible : Visibility.Collapsed;
        ActivitySplitter.ResizeDirection = side ? GridResizeDirection.Columns : GridResizeDirection.Rows;
        ActivitySplitter.HorizontalAlignment = side ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        ActivitySplitter.VerticalAlignment = side ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        ActivitySplitter.Width = side ? 1 : double.NaN;
        ActivitySplitter.Height = side ? double.NaN : 1;

        Grid.SetRow(DetailsArea, side ? 1 : 3);
        Grid.SetColumn(DetailsArea, side ? 2 : 0);
        DetailsArea.Visibility = details ? Visibility.Visible : Visibility.Collapsed;
        DetailsArea.Margin = side ? new Thickness(8, 0, 16, 12) : new Thickness(16, 8, 16, 12);

        // Details: graph beside the connections, or above them when they share a narrow column.
        var dc = DetailsArea.ColumnDefinitions;
        var dr = DetailsArea.RowDefinitions;
        dc[0].Width = side ? new GridLength(1, GridUnitType.Star) : new GridLength(2, GridUnitType.Star);
        dc[1].Width = new GridLength(side ? 0 : 16);
        dc[2].Width = side ? new GridLength(0) : new GridLength(3, GridUnitType.Star);
        dr[0].Height = side ? new GridLength(200) : new GridLength(1, GridUnitType.Star);
        dr[1].Height = new GridLength(side ? 12 : 0);
        dr[2].Height = side ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(ConnectionsPanel, side ? 2 : 0);
        Grid.SetColumn(ConnectionsPanel, side ? 0 : 2);

        // Compact and side by side: only traffic columns, in the list and on the Total line.
        bool compact = layout is ActivityLayout.Compact or ActivityLayout.SideBySide;
        var header = TotalHeader.ColumnDefinitions;
        _headerWidths ??= header.Select(c => c.Width).ToArray();
        foreach (int i in RuleColumns)
        {
            AppsGrid.Columns[i].Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            header[i].Width = compact ? new GridLength(0) : _headerWidths[i];
        }
        foreach (UIElement child in TotalHeader.Children)
            if (RuleColumns.Contains(Grid.GetColumn(child)))
                child.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LimitBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var box = (TextBox)sender;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        box.SelectAll();
    }

    private void EditQuota_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AppRowViewModel row) return;
        var dialog = new QuotaWindow(row.Name, row.Quota) { Owner = this };
        if (dialog.ShowDialog() == true) row.Quota = dialog.Result;
    }

    private void AutoStartBox_Click(object sender, RoutedEventArgs e)
    {
        bool wanted = AutoStartBox.IsChecked == true;
        if (!AutoStart.Set(wanted))
            MessageBox.Show(this, "Impossible de modifier le dÃ©marrage automatique (tÃ¢che planifiÃ©e).", "WebLimiter",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        AutoStartBox.IsChecked = AutoStart.IsEnabled();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs) return;
        if (Tabs.SelectedItem == StatsTab || Tabs.SelectedItem == PeaksTab) _vm.Stats.Refresh();
        if (Tabs.SelectedItem == ExclusionsTab) _vm.Exclusions.Refresh();
        if (Tabs.SelectedItem == SettingsTab) _vm.Settings.Refresh();
    }

    private void RefreshExclusions_Click(object sender, RoutedEventArgs e) => _vm.Exclusions.Refresh();

    private void AddProgram_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Programme Ã  bloquer",
            Filter = "Programmes (*.exe)|*.exe",
        };
        if (dialog.ShowDialog(this) == true) _vm.Exclusions.AddProgram(dialog.FileName);
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_vm.Settings.DataFolder}\""));

    private void RefreshStats_Click(object sender, RoutedEventArgs e) => _vm.Stats.Refresh();

    private void ClearStats_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Effacer tout l'historique de consommation ? Les quotas repartiront de zÃ©ro.\nCette action est dÃ©finitive.",
            "Effacer l'historique", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) _vm.Stats.Clear();
    }

    /// <summary>Numeric columns sort largest first on the first click; the name column starts Aâ†’Z.</summary>
    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        string member = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(member)) return;

        bool text = member == nameof(AppRowViewModel.Name);
        var first = text ? ListSortDirection.Ascending : ListSortDirection.Descending;
        var next = e.Column.SortDirection switch
        {
            null => first,
            ListSortDirection.Ascending => ListSortDirection.Descending,
            _ => ListSortDirection.Ascending,
        };

        var view = (ListCollectionView)_vm.AppsView;
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(member, next));
            if (!text) view.SortDescriptions.Add(new SortDescription(nameof(AppRowViewModel.Name), ListSortDirection.Ascending));
            if (!view.LiveSortingProperties.Contains(member)) view.LiveSortingProperties.Add(member);
        }
        foreach (var c in AppsGrid.Columns) c.SortDirection = null;
        e.Column.SortDirection = next;
    }
}
