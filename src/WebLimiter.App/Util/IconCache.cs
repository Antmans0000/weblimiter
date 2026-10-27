using System;
using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WebLimiter.App.Util;

/// <summary>Extracts and caches the icon of an executable as a WPF image.</summary>
public static class IconCache
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ImageSource? Generic = FromIcon(System.Drawing.SystemIcons.Application);
    private static readonly ImageSource? Service = FromIcon(System.Drawing.SystemIcons.Shield);

    public static ImageSource? Get(string path) => Cache.GetOrAdd(path, Load);

    private static ImageSource? Load(string path)
    {
        try
        {
            if (path.StartsWith("svc:", StringComparison.OrdinalIgnoreCase)) return Service;
            if (!File.Exists(path)) return Generic;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            return icon == null ? Generic : FromIcon(icon);
        }
        catch { return Generic; }
    }

    private static ImageSource? FromIcon(System.Drawing.Icon icon)
    {
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }
}
