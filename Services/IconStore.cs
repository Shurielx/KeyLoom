using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Keyloom.Services;

/// <summary>Custom macro icons, copied into the app folder as small PNGs so profiles never point at files elsewhere.</summary>
public static partial class IconStore
{
    private const int Size = 96;
    public static string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Keyloom", "icons");
    [GeneratedRegex(@"^[0-9a-f]{32}\.png$")] private static partial Regex NamePattern();
    public static bool IsValidName(string name) => NamePattern().IsMatch(name);
    /// <summary>Scales the picture to fit a square icon and stores it. Returns the stored file name.</summary>
    public static string Import(string source)
    {
        if (new FileInfo(source).Length > 20 * 1024 * 1024) throw new ArgumentException("Choose an image smaller than 20 MB.");
        BitmapSource image;
        try
        {
            var decoded = new BitmapImage();
            decoded.BeginInit();
            decoded.CacheOption = BitmapCacheOption.OnLoad;
            decoded.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            decoded.DecodePixelWidth = Size * 2;
            decoded.UriSource = new Uri(Path.GetFullPath(source));
            decoded.EndInit();
            image = decoded;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or UriFormatException)
        { throw new ArgumentException("This file is not an image Keyloom can read. Use PNG, JPG, BMP, GIF or ICO."); }
        // Crop to a centered square, then scale down.
        double side = Math.Min(image.Width, image.Height);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double scale = Size / side;
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawImage(image, new System.Windows.Rect(-(image.Width - side) / 2, -(image.Height - side) / 2, image.Width, image.Height));
        }
        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        bitmap.Render(visual);
        Directory.CreateDirectory(Folder);
        string name = Guid.NewGuid().ToString("N") + ".png";
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(Folder, name))) encoder.Save(stream);
        return name;
    }
    /// <summary>Loads a stored icon without locking the file, or null when it is missing or unreadable.</summary>
    public static ImageSource? Load(string name)
    {
        if (!IsValidName(name)) return null;
        string path = Path.Combine(Folder, name);
        if (!File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit(); image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException) { return null; }
    }
    /// <summary>Deletes stored icons no profile uses any more.</summary>
    public static void Prune(IEnumerable<string> used)
    {
        if (!Directory.Exists(Folder)) return;
        var keep = used.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(Folder, "*.png"))
        {
            string name = Path.GetFileName(file);
            if (!IsValidName(name) || keep.Contains(name)) continue;
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
