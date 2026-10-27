using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ProxyManager.Services;

/// <summary>Display name and icon of an executable, cached per path.</summary>
public static class ExeInfo
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase);

    public static string GetDescription(string path) => Descriptions.GetOrAdd(path, static p =>
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(p).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(description)) return description;
        }
        catch (Exception e) when (e is FileNotFoundException or ArgumentException)
        {
        }
        return Path.GetFileNameWithoutExtension(p);
    });

    public static ImageSource? GetIcon(string path) => Icons.GetOrAdd(path, static p =>
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(p);
            if (icon is null) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is FileNotFoundException or ArgumentException or IOException)
        {
            return null;
        }
    });
}
