using System;
using System.Linq;
using System.Threading;
using System.Windows;
using WebLimiter.App.Util;
using WebLimiter.App.ViewModels;
using WebLimiter.Core.Backend;
using WebLimiter.Core.Storage;
using Forms = System.Windows.Forms;

namespace WebLimiter.App;

public partial class App : Application
{
    private const string InstanceName = "Global\\WebLimiter.SingleInstance";
    private const string ShowEventName = "Global\\WebLimiter.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private SwitchableBackend? _backend;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _toggleItem;
    private bool _trayHintShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The debug screenshot mode may run next to a normal instance.
        bool screenshotMode = e.Args.Contains("--screenshot");
        _mutex = new Mutex(true, screenshotMode ? InstanceName + ".Screenshot" : InstanceName, out bool first);
        if (!first)
        {
            // Another instance runs: bring it to front and quit.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* not ready yet */ }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
            screenshotMode ? ShowEventName + ".Screenshot" : ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(ShowWindow);
        }) { IsBackground = true }.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            DataPaths.LogError("Exception non gérée (UI)", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DataPaths.LogError("Exception non gérée", args.ExceptionObject as Exception);

        _backend = new SwitchableBackend(CreateBackend());
        _vm = new MainViewModel(_backend);

        _window = new MainWindow(_vm);
        _window.Closing += (_, args) =>
        {
            if (_exiting) return;
            args.Cancel = true;
            _window.Hide();
            if (!_trayHintShown && _tray != null)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(3000, "WebLimiter", "WebLimiter continue de fonctionner dans la zone de notification.",
                    Forms.ToolTipIcon.Info);
            }
        };
        CreateTray();
        _vm.Refreshed += UpdateTray;

        if (!e.Args.Contains("--tray")) ShowWindow();

        int shot = Array.IndexOf(e.Args, "--screenshot");
        if (shot >= 0 && shot + 1 < e.Args.Length) ScreenshotAndExit(e.Args[shot + 1]);
    }

    /// <summary>Debug aid: renders the window to a PNG after a few seconds of traffic, then quits.</summary>
    private async void ScreenshotAndExit(string path)
    {
        await System.Threading.Tasks.Task.Delay(8000);
        if (path.Contains("stats")) _window!.Tabs.SelectedItem = _window.StatsTab;
        else if (path.Contains("peaks")) _window!.Tabs.SelectedItem = _window.PeaksTab;
        else if (path.Contains("excl")) _window!.Tabs.SelectedItem = _window.ExclusionsTab;
        else if (path.Contains("settings")) _window!.Tabs.SelectedItem = _window.SettingsTab;
        else if (_vm!.AppsView.Count > 0) _vm.Selected = (AppRowViewModel)_vm.AppsView.GetItemAt(0);
        await System.Threading.Tasks.Task.Delay(2500);
        var root = (System.Windows.FrameworkElement)_window!.Content;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_window);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var fs = System.IO.File.Create(path)) enc.Save(fs);
        ExitApp();
    }

    private bool _exiting;

    /// <summary>Uses the WebLimiter service when installed, otherwise runs the engine in-process.</summary>
    private static IBackend CreateBackend()
    {
        var service = ServiceInstaller.Query();
        if (service != null)
        {
            try { ServiceInstaller.Start(); }
            catch (Exception ex) { DataPaths.LogError("Démarrage du service impossible", ex); }
            var pipe = new PipeBackend();
            pipe.WaitUntilReady(TimeSpan.FromSeconds(5));
            return pipe;
        }
        var local = new LocalBackend();
        local.SetFiltering(true);
        return local;
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Ouvrir WebLimiter", null, (_, _) => ShowWindow());
        _toggleItem = new Forms.ToolStripMenuItem("Suspendre le filtrage", null, (_, _) =>
            _vm!.ToggleEngineCommand.Execute(null));
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => ExitApp());

        using var stream = GetResourceStream(new Uri("pack://application:,,,/Assets/weblimiter.ico"))!.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "WebLimiter",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) ShowWindow(); };
    }

    private void UpdateTray()
    {
        if (_tray == null || _vm == null) return;
        string text = _vm.IsRunning ? $"WebLimiter\n↓ {_vm.TotalDownText}\n↑ {_vm.TotalUpText}" : "WebLimiter (suspendu)";
        _tray.Text = text.Length > 63 ? text[..63] : text;
        _toggleItem!.Text = _vm.IsRunning ? "Suspendre le filtrage" : "Reprendre le filtrage";
    }

    public void ShowWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void ExitApp()
    {
        _exiting = true;
        _window?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _backend?.Dispose();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
