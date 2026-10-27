using System;
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

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.Refreshed += () => Graph.Invalidate();

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => vm.Refresh(), Dispatcher);
        _timer.Start();
        vm.Refresh();

        AutoStartBox.IsChecked = AutoStart.IsEnabled();
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
            MessageBox.Show(this, "Impossible de modifier le démarrage automatique (tâche planifiée).", "WebLimiter",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        AutoStartBox.IsChecked = AutoStart.IsEnabled();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs) return;
        if (Tabs.SelectedIndex == 1) _vm.Stats.Refresh();
        if (Tabs.SelectedIndex == 2) _vm.Exclusions.Refresh();
        if (Tabs.SelectedIndex == 3) _vm.Settings.Refresh();
    }

    private void RefreshExclusions_Click(object sender, RoutedEventArgs e) => _vm.Exclusions.Refresh();

    private void AddProgram_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Programme à bloquer",
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
            "Effacer tout l'historique de consommation ? Les quotas repartiront de zéro.\nCette action est définitive.",
            "Effacer l'historique", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) _vm.Stats.Clear();
    }
}
