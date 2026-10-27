using WebLimiter.App.Util;
using WebLimiter.Core.Engine;

namespace WebLimiter.App.ViewModels;

public sealed class ConnectionRow : ObservableObject
{
    private ConnectionSnapshot _snap;
    private string? _host;

    public ConnectionRow(ConnectionSnapshot snap)
    {
        _snap = snap;
        Key = MakeKey(snap);
        if (System.Net.IPAddress.TryParse(snap.RemoteAddress, out var ip))
            _host = ReverseDns.TryGet(ip,
                name => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Host = name));
    }

    public static string MakeKey(ConnectionSnapshot s) => $"{s.Protocol}|{s.LocalPort}|{s.RemoteAddress}|{s.RemotePort}";

    public string Key { get; }
    public string Protocol => _snap.Protocol;
    public int LocalPort => _snap.LocalPort;
    public string Remote => _snap.RemoteAddress;
    public int RemotePort => _snap.RemotePort;
    public string? Host { get => _host; private set => Set(ref _host, value); }
    public double DownRate => _snap.DownRate;
    public double UpRate => _snap.UpRate;
    public string DownRateText => Format.Rate(_snap.DownRate);
    public string UpRateText => Format.Rate(_snap.UpRate);
    public long TotalDown => _snap.TotalDown;
    public long TotalUp => _snap.TotalUp;
    public string TotalDownText => Format.Bytes(_snap.TotalDown);
    public string TotalUpText => Format.Bytes(_snap.TotalUp);

    public void Update(ConnectionSnapshot snap)
    {
        _snap = snap;
        Raise(nameof(DownRate));
        Raise(nameof(UpRate));
        Raise(nameof(DownRateText));
        Raise(nameof(UpRateText));
        Raise(nameof(TotalDown));
        Raise(nameof(TotalUp));
        Raise(nameof(TotalDownText));
        Raise(nameof(TotalUpText));
    }
}
