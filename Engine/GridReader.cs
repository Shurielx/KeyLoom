using System.Globalization;
using Keyloom.Models;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace Keyloom.Engine;

/// <summary>Turns a picture of a grid into the number in each cell, row by row; null for a cell without one.</summary>
public interface IGridReader
{
    long?[] Read(PixelImage image, int rows, int columns);
}

/// <summary>One look at a grid: the numbers in its cells and where each cell's middle is on the screen.</summary>
public sealed class GridReading(int rows, int columns, long?[] values, (int X, int Y)[] centers)
{
    public int Rows { get; } = rows;
    public int Columns { get; } = columns;
    public long?[] Values { get; } = values;
    public (int X, int Y)[] Centers { get; } = centers;
    public long? this[int row, int column] => Values[row * Columns + column];

    /// <summary>The cell a Click on a cell step picks, or -1. Ties go to the first cell, reading row by row.</summary>
    public int Pick(CellPick pick, long value = 0)
    {
        int best = -1;
        for (int i = 0; i < Values.Length; i++)
        {
            var v = Values[i];
            bool better = pick switch
            {
                CellPick.Highest => v != null && (best < 0 || v > Values[best]),
                CellPick.Lowest => v != null && (best < 0 || v < Values[best]),
                CellPick.Equals => v == value && best < 0,
                _ => v == null && best < 0
            };
            if (better) best = i;
        }
        return best;
    }
    /// <summary>
    /// Two cells with the same number, the lowest number first, as (from, to): the later cell is dragged onto the earlier one.
    /// Null when no two cells match.
    /// </summary>
    public (int From, int To)? Pair()
    {
        (int From, int To, long Value)? best = null;
        for (int a = 0; a < Values.Length; a++)
        {
            if (Values[a] is not { } v || best is { } b && v >= b.Value) continue;
            for (int c = a + 1; c < Values.Length; c++)
                if (Values[c] == v) { best = (c, a, v); break; }
        }
        return best is { } found ? (found.From, found.To) : null;
    }
    /// <summary>The grid as text, one row per line, a dot for an empty cell.</summary>
    public override string ToString() => string.Join("\n", Enumerable.Range(0, Rows).Select(r =>
        string.Join("  ", Enumerable.Range(0, Columns).Select(c => this[r, c]?.ToString(CultureInfo.InvariantCulture) ?? "·"))));
}

/// <summary>Reads numbers with the OCR built into Windows: one pass over the whole grid, each number put in the cell it sits in.</summary>
public sealed class WindowsOcrReader : IGridReader
{
    public static readonly WindowsOcrReader Instance = new();
    /// <summary>OCR reads text best when it is at least this many pixels tall; smaller grids are enlarged first.</summary>
    private const int CellHeightWanted = 60;
    private OcrEngine? _engine;

    public long?[] Read(PixelImage image, int rows, int columns)
    {
        var values = new long?[rows * columns];
        if (image.Width == 0 || image.Height == 0) return values;
        _engine ??= OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US")) ?? OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException(L.T("Windows has no text recognition installed. Add a language with OCR in Windows Settings, Time & language."));
        int scale = Math.Clamp((int)Math.Ceiling(CellHeightWanted / Math.Max(1.0, (double)image.Height / rows)), 1, 4);
        while (scale > 1 && Math.Max(image.Width, image.Height) * scale > OcrEngine.MaxImageDimension) scale--;
        var big = Enlarge(image, scale);
        var bytes = new byte[big.Pixels.Length * 4];
        Buffer.BlockCopy(big.Pixels, 0, bytes, 0, bytes.Length);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(CryptographicBuffer.CreateFromByteArray(bytes), BitmapPixelFormat.Bgra8, big.Width, big.Height, BitmapAlphaMode.Ignore);
        var result = _engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
        double cellWidth = (double)big.Width / columns, cellHeight = (double)big.Height / rows;
        foreach (var word in result.Lines.SelectMany(l => l.Words))
        {
            if (Number(word.Text) is not { } number) continue;
            var box = word.BoundingRect;
            int column = Math.Clamp((int)((box.X + box.Width / 2) / cellWidth), 0, columns - 1);
            int row = Math.Clamp((int)((box.Y + box.Height / 2) / cellHeight), 0, rows - 1);
            int cell = row * columns + column;
            // Two pieces in one cell, like "1 024" read as two words, join up in reading order.
            values[cell] = values[cell] is { } before && number < 1000 && before < 1_000_000_000 ? before * 1000 + number : number;
        }
        return values;
    }
    /// <summary>Each pixel becomes a square of pixels, so small digits keep sharp edges.</summary>
    private static PixelImage Enlarge(PixelImage image, int scale)
    {
        if (scale == 1) return image;
        int w = image.Width * scale, h = image.Height * scale;
        var pixels = new uint[w * h];
        for (int y = 0; y < h; y++)
        {
            int from = (y / scale) * image.Width, to = y * w;
            for (int x = 0; x < w; x++) pixels[to + x] = image.Pixels[from + x / scale];
        }
        return new(w, h, pixels);
    }
    /// <summary>
    /// The number in a piece of recognized text: "32", "1,024", "1.5K" or "2M". Letters OCR mixes up with digits count as
    /// those digits. Text with no digits is not a number.
    /// </summary>
    public static long? Number(string text)
    {
        var t = new string(text.Trim().Select(c => c switch { 'O' or 'o' => '0', 'l' or 'I' or '|' => '1', _ => c }).ToArray());
        double multiplier = 1;
        if (t.Length > 1 && char.ToUpperInvariant(t[^1]) is 'K' or 'M' or 'B')
        {
            multiplier = char.ToUpperInvariant(t[^1]) switch { 'K' => 1e3, 'M' => 1e6, _ => 1e9 };
            t = t[..^1];
        }
        t = t.Replace(",", "").Replace(" ", "");
        if (t.Length == 0 || !t.Any(char.IsDigit) || !t.All(c => char.IsDigit(c) || c == '.')) return null;
        if (!double.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value)) return null;
        // A dot without a K or M is a thousands mark, as in "1.024".
        if (multiplier == 1 && t.Contains('.')) value = double.Parse(t.Replace(".", ""), CultureInfo.InvariantCulture);
        return value * multiplier > long.MaxValue / 2 ? null : (long)Math.Round(value * multiplier);
    }
}

/// <summary>Reads a macro's grids from the screen while it runs.</summary>
public sealed class GridLocator(IScreen screen, IWindowSystem windows, IntPtr window, IEnumerable<ScreenGrid> grids, IGridReader reader)
{
    private readonly Dictionary<string, ScreenGrid> _grids = grids.ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);
    public bool Has(string? name) => _grids.ContainsKey(name ?? "");
    /// <summary>Where the grid is on the screen now.</summary>
    public (int X, int Y, int Width, int Height) Area(string? name) { var g = Get(name); return ScreenAreas.Resolve(screen, windows, window, g.Area, g.Name); }
    public ScreenGrid Get(string? name) => _grids.TryGetValue(name ?? "", out var g) ? g : throw new ArgumentException(L.T($"There is no grid called '{name}'."));
    public GridReading Read(string? name)
    {
        var grid = Get(name);
        var (x, y, width, height) = ScreenAreas.Resolve(screen, windows, window, grid.Area, grid.Name);
        var shot = screen.Capture(x, y, width, height);
        var values = reader.Read(shot, grid.Rows, grid.Columns);
        var centers = new (int X, int Y)[grid.Rows * grid.Columns];
        for (int r = 0; r < grid.Rows; r++)
            for (int c = 0; c < grid.Columns; c++)
                centers[r * grid.Columns + c] = (x + (int)((c + 0.5) * width / grid.Columns), y + (int)((r + 0.5) * height / grid.Rows));
        return new(grid.Rows, grid.Columns, values, centers);
    }
}

public static class ScreenAreas
{
    /// <summary>An area in screen pixels, cut to what the monitors show. Window areas follow the window.</summary>
    public static (int X, int Y, int Width, int Height) Resolve(IScreen screen, IWindowSystem windows, IntPtr window, ScreenArea area, string owner)
    {
        if (area.Space == CoordinateSpace.Window && window == IntPtr.Zero) throw new InvalidOperationException(L.T($"'{owner}' is in the target window, which was not found."));
        var (left, top) = area.Space == CoordinateSpace.Window ? windows.ClientToScreen(window, area.X, area.Y) : (area.X, area.Y);
        var bounds = screen.Bounds;
        int x1 = Math.Max(left, bounds.X), y1 = Math.Max(top, bounds.Y);
        int x2 = Math.Min(left + area.Width, bounds.X + bounds.Width), y2 = Math.Min(top + area.Height, bounds.Y + bounds.Height);
        return (x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
    }
}
