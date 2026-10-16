using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Keyloom.Models;

namespace Keyloom.Engine;

/// <summary>Turns target pictures and colors to and from the text kept in macro files.</summary>
public static class TargetPicture
{
    public static PixelImage Decode(string base64)
    {
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(base64));
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            if (frame.PixelWidth > ScreenTarget.MaxPictureSide || frame.PixelHeight > ScreenTarget.MaxPictureSide)
                throw new ArgumentException(L.T($"A target picture can be up to {ScreenTarget.MaxPictureSide} by {ScreenTarget.MaxPictureSide} pixels."));
            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var pixels = new int[bgra.PixelWidth * bgra.PixelHeight];
            bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
            return new(bgra.PixelWidth, bgra.PixelHeight, (uint[])(object)pixels);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or FileFormatException or IOException or InvalidOperationException)
        { throw new ArgumentException(L.T("The target picture could not be read. Capture it again."), ex); }
    }
    public static string Encode(PixelImage image)
    {
        var source = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, (int[])(object)image.Pixels, image.Width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }
    /// <summary>Reads "#RRGGBB" (the # may be left out).</summary>
    public static uint ParseColor(string text)
    {
        string hex = (text ?? "").Trim().TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            throw new ArgumentException(L.T($"\"{text}\" is not a color. Write it as #RRGGBB, like #FF8800."));
        return 0xFF000000 | rgb;
    }
    public static string ColorText(uint color) => $"#{color & 0xFFFFFF:X6}";
}

/// <summary>Finds a macro's targets on the screen while it runs. Pictures are read once and kept.</summary>
public sealed class TargetLocator(IScreen screen, IWindowSystem windows, IntPtr window, IEnumerable<ScreenTarget> targets)
{
    private readonly Dictionary<string, ScreenTarget> _targets = targets.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PixelImage> _pictures = new(StringComparer.OrdinalIgnoreCase);
    public ScreenTarget Get(string? name) => _targets.TryGetValue(name ?? "", out var t) ? t : throw new ArgumentException(L.T($"There is no target called '{name}'."));

    /// <summary>Where the target is on the screen right now, in screen pixels, or null when it isn't there.</summary>
    public Match? Locate(string? name)
    {
        var target = Get(name);
        var (x, y, width, height) = SearchArea(target);
        if (width <= 0 || height <= 0) return null;
        var shot = screen.Capture(x, y, width, height);
        Match? found = target.Kind == ScreenTargetKind.Color
            ? TargetFinder.FindColor(shot, TargetPicture.ParseColor(target.Color), target.Tolerance)
            : TargetFinder.FindPicture(shot, Picture(target), target.Tolerance);
        return found is { } m ? m with { X = m.X + x, Y = m.Y + y } : null;
    }
    private PixelImage Picture(ScreenTarget target)
    {
        if (!_pictures.TryGetValue(target.Name, out var picture)) _pictures[target.Name] = picture = TargetPicture.Decode(target.Picture);
        return picture;
    }
    /// <summary>The rectangle to look in, in screen pixels, cut to what the monitors show.</summary>
    public (int X, int Y, int Width, int Height) SearchArea(ScreenTarget target)
    {
        var bounds = screen.Bounds;
        (int X, int Y, int Width, int Height) area = bounds;
        if (target.Area is { } a)
        {
            if (a.Space == CoordinateSpace.Window && window == IntPtr.Zero) throw new InvalidOperationException(L.T($"'{target.Name}' looks in the target window, which was not found."));
            var (left, top) = a.Space == CoordinateSpace.Window ? windows.ClientToScreen(window, a.X, a.Y) : (a.X, a.Y);
            area = (left, top, a.Width, a.Height);
        }
        else if (window != IntPtr.Zero)
        {
            var (w, h) = windows.ClientSize(window);
            var (left, top) = windows.ClientToScreen(window, 0, 0);
            area = (left, top, w, h);
        }
        int x1 = Math.Max(area.X, bounds.X), y1 = Math.Max(area.Y, bounds.Y);
        int x2 = Math.Min(area.X + area.Width, bounds.X + bounds.Width), y2 = Math.Min(area.Y + area.Height, bounds.Y + bounds.Height);
        return (x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
    }
}
