namespace Keyloom.Engine;

/// <summary>Where a target was found, in the pixels of the image that was searched.</summary>
public readonly record struct Match(int X, int Y, int Width, int Height)
{
    public (int X, int Y) Center => (X + Width / 2, Y + Height / 2);
}

/// <summary>Finds a picture or a color in a screen capture.</summary>
public static class TargetFinder
{
    /// <summary>Tolerance 0 to 100 as the difference allowed per color channel, 0 to 255.</summary>
    public static int ChannelTolerance(int tolerance) => (int)Math.Round(Math.Clamp(tolerance, 0, 100) * 2.55);

    /// <summary>
    /// The first spot, top to bottom, where the picture shows with at most the tolerance as the average difference.
    /// A few telling pixels are checked first, so most spots are ruled out after one or two reads.
    /// </summary>
    public static Match? FindPicture(PixelImage screen, PixelImage picture, int tolerance)
    {
        int w = picture.Width, h = picture.Height;
        if (w == 0 || h == 0 || w > screen.Width || h > screen.Height) return null;
        int mean = ChannelTolerance(tolerance);
        // One pixel may stray further than the average, like antialiased text over a changing background.
        int pixel = Math.Min(255, mean * 3 + 24);
        long allowed = (long)mean * 3 * w * h;
        var samples = Samples(picture);
        uint[] s = screen.Pixels, p = picture.Pixels;
        for (int y = 0; y <= screen.Height - h; y++)
            for (int x = 0; x <= screen.Width - w; x++)
            {
                bool close = true;
                foreach (var (sx, sy, color) in samples)
                    if (Distance(s[(y + sy) * screen.Width + x + sx], color) > pixel) { close = false; break; }
                if (!close || Difference(screen, picture, x, y, allowed) > allowed) continue;
                return Best(screen, picture, x, y, allowed);
            }
        return null;
    }
    /// <summary>The first match is often one pixel off the best one; look around it for the closest fit.</summary>
    private static Match Best(PixelImage screen, PixelImage picture, int x, int y, long allowed)
    {
        (int X, int Y, long Diff) best = (x, y, Difference(screen, picture, x, y, allowed));
        for (int dy = 0; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if ((dx == 0 && dy == 0) || nx < 0 || nx > screen.Width - picture.Width || ny > screen.Height - picture.Height) continue;
                long diff = Difference(screen, picture, nx, ny, best.Diff);
                if (diff < best.Diff) best = (nx, ny, diff);
            }
        return new(best.X, best.Y, picture.Width, picture.Height);
    }
    /// <summary>Sum of channel differences, stopping early once it passes the limit.</summary>
    private static long Difference(PixelImage screen, PixelImage picture, int x, int y, long limit)
    {
        long sum = 0;
        uint[] s = screen.Pixels, p = picture.Pixels;
        for (int py = 0; py < picture.Height; py++)
        {
            int row = (y + py) * screen.Width + x, prow = py * picture.Width;
            for (int px = 0; px < picture.Width; px++) sum += Sum(s[row + px], p[prow + px]);
            if (sum > limit) return sum;
        }
        return sum;
    }
    /// <summary>Up to 12 pixels spread over the picture, the ones least like its average color first.</summary>
    private static List<(int X, int Y, uint Color)> Samples(PixelImage picture)
    {
        var spots = new List<(int X, int Y, uint Color)>();
        for (int gy = 0; gy < 5; gy++)
            for (int gx = 0; gx < 5; gx++)
            {
                int x = (picture.Width - 1) * gx / 4, y = (picture.Height - 1) * gy / 4;
                if (!spots.Any(s => s.X == x && s.Y == y)) spots.Add((x, y, picture[x, y]));
            }
        long r = 0, g = 0, b = 0;
        foreach (uint c in picture.Pixels) { r += (c >> 16) & 0xFF; g += (c >> 8) & 0xFF; b += c & 0xFF; }
        int n = picture.Pixels.Length;
        uint average = (uint)(((r / n) << 16) | ((g / n) << 8) | (b / n));
        return spots.OrderByDescending(s => Sum(s.Color, average)).Take(12).ToList();
    }

    /// <summary>The first patch of the color, top to bottom, with the spot nearest its middle that has the color.</summary>
    public static Match? FindColor(PixelImage screen, uint color, int tolerance)
    {
        int limit = ChannelTolerance(tolerance);
        uint[] s = screen.Pixels;
        for (int i = 0; i < s.Length; i++)
        {
            if (Distance(s[i], color) > limit) continue;
            // Grow the patch from here to find how big it is.
            int left = i % screen.Width, right = left, top = i / screen.Width, bottom = top;
            var seen = new HashSet<int> { i };
            var todo = new Stack<int>(); todo.Push(i);
            while (todo.Count > 0 && seen.Count < 200_000)
            {
                int at = todo.Pop(), x = at % screen.Width, y = at / screen.Width;
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                foreach (int next in new[] { x > 0 ? at - 1 : -1, x < screen.Width - 1 ? at + 1 : -1, y > 0 ? at - screen.Width : -1, y < screen.Height - 1 ? at + screen.Width : -1 })
                    if (next >= 0 && !seen.Contains(next) && Distance(s[next], color) <= limit) { seen.Add(next); todo.Push(next); }
            }
            int cx = (left + right) / 2, cy = (top + bottom) / 2;
            // A ring or an L has no color in its middle; click the nearest spot that has it.
            int spot = seen.MinBy(p => (long)(p % screen.Width - cx) * (p % screen.Width - cx) + (long)(p / screen.Width - cy) * (p / screen.Width - cy));
            int width = right - left + 1, height = bottom - top + 1;
            return new(spot % screen.Width - width / 2, spot / screen.Width - height / 2, width, height);
        }
        return null;
    }

    /// <summary>The largest difference in any one channel.</summary>
    public static int Distance(uint a, uint b) => Math.Max(Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF)),
        Math.Max(Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)), Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF))));
    private static int Sum(uint a, uint b) => Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF))
        + Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)) + Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF));
}
