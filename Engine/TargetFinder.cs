namespace Keyloom.Engine;

/// <summary>
/// Where a target was found, in the pixels of the image that was searched. Scale is how large the picture showed
/// compared with when it was captured: 1 unless it was looked for at other sizes.
/// </summary>
public readonly record struct Match(int X, int Y, int Width, int Height, double Scale = 1)
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
    /// <summary>How much smaller or larger than captured <see cref="FindPictureAnySize"/> looks for a picture.</summary>
    public const double MinScale = 0.8, MaxScale = 1.2;
    /// <summary>
    /// The picture as captured, or else the spot where it shows best between 80 and 120 percent of its size, like a game
    /// board after the camera zoomed. Sizes are tried on a shrunk copy of the screen first, then the best few are checked
    /// closely, one percent at a time.
    /// </summary>
    public static Match? FindPictureAnySize(PixelImage screen, PixelImage picture, int tolerance)
    {
        if (FindPicture(screen, picture, tolerance) is { } exact) return exact;
        int w = picture.Width, h = picture.Height;
        if (w < 4 || h < 4 || w * MinScale > screen.Width || h * MinScale > screen.Height) return null;
        int mean = ChannelTolerance(tolerance);
        // Shrink so the picture keeps at least 8 pixels on its short side, and step sizes so its edges move about a pixel.
        int f = Math.Clamp((int)(Math.Min(w, h) * MinScale / 8), 1, 8);
        PixelImage small = Shrink(screen, f), shrunk = Shrink(picture, f);
        double step = Math.Clamp(1.5 * f / Math.Max(w, h), 0.01, 0.05);
        var rough = new List<(int X, int Y, double Scale, double Diff)>();
        for (double scale = MinScale; scale <= MaxScale + 1e-9; scale += step)
            if (Rough(small, Resize(shrunk, (int)Math.Round(w * scale / f), (int)Math.Round(h * scale / f)), mean) is { } spot)
                rough.Add((spot.X * f, spot.Y * f, scale, spot.Diff));
        (int X, int Y, int Width, int Height, int Percent, long Diff)? best = null;
        var sized = new Dictionary<int, PixelImage>();
        foreach (var (rx, ry, rs, _) in rough.OrderBy(r => r.Diff).Take(3))
            for (int percent = (int)Math.Round((rs - step) * 100); percent <= (int)Math.Round((rs + step) * 100); percent++)
            {
                if (percent < MinScale * 100 || percent > MaxScale * 100) continue;
                if (!sized.TryGetValue(percent, out var pic))
                    sized[percent] = pic = Resize(picture, (int)Math.Round(w * percent / 100.0), (int)Math.Round(h * percent / 100.0));
                long n = pic.Width * pic.Height;
                // Scaling blurs edges a little, so allow a few more steps of difference than for the picture as captured.
                long allowed = (long)(mean + 8) * 3 * n;
                for (int y = Math.Max(0, ry - f - 1); y <= Math.Min(screen.Height - pic.Height, ry + f + 1); y++)
                    for (int x = Math.Max(0, rx - f - 1); x <= Math.Min(screen.Width - pic.Width, rx + f + 1); x++)
                    {
                        // Compared per pixel, so a smaller size does not win just for having fewer pixels.
                        long limit = best is { } b ? Math.Min(allowed, b.Diff * n / (b.Width * b.Height)) : allowed;
                        long diff = Difference(screen, pic, x, y, limit);
                        if (diff <= limit && (best is not { } c || diff * c.Width * c.Height < c.Diff * n))
                            best = (x, y, pic.Width, pic.Height, percent, diff);
                    }
            }
        return best is { } m ? new Match(m.X, m.Y, m.Width, m.Height, m.Percent / 100.0) : null;
    }
    /// <summary>The spot on a shrunk screen that looks most like the picture, with its difference per pixel, if any is close.</summary>
    private static (int X, int Y, double Diff)? Rough(PixelImage screen, PixelImage picture, int mean)
    {
        int w = picture.Width, h = picture.Height;
        if (w > screen.Width || h > screen.Height) return null;
        // Shrinking and resizing smear edges, so this pass is loose and lets one telling pixel be off.
        int pixel = Math.Min(255, mean * 3 + 40);
        long allowed = (long)(mean + 20) * 3 * w * h;
        var samples = Samples(picture);
        uint[] s = screen.Pixels;
        (int X, int Y, long Diff)? best = null;
        for (int y = 0; y <= screen.Height - h; y++)
            for (int x = 0; x <= screen.Width - w; x++)
            {
                int off = 0;
                foreach (var (sx, sy, color) in samples)
                    if (Distance(s[(y + sy) * screen.Width + x + sx], color) > pixel && ++off > 1) break;
                if (off > 1) continue;
                long limit = best?.Diff - 1 ?? allowed;
                long diff = Difference(screen, picture, x, y, limit);
                if (diff <= limit) best = (x, y, diff);
            }
        return best is { } b ? (b.X, b.Y, (double)b.Diff / (w * h)) : null;
    }
    /// <summary>The image made smaller by a whole factor, each pixel the average of the square it covers.</summary>
    private static PixelImage Shrink(PixelImage image, int factor)
    {
        if (factor == 1) return image;
        int w = image.Width / factor, h = image.Height / factor, area = factor * factor;
        var pixels = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int r = 0, g = 0, b = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int row = (y * factor + dy) * image.Width + x * factor;
                    for (int dx = 0; dx < factor; dx++)
                    {
                        uint c = image.Pixels[row + dx];
                        r += (int)((c >> 16) & 0xFF); g += (int)((c >> 8) & 0xFF); b += (int)(c & 0xFF);
                    }
                }
                pixels[y * w + x] = 0xFF000000 | (uint)(r / area << 16 | g / area << 8 | b / area);
            }
        return new(w, h, pixels);
    }
    /// <summary>The image drawn at another size, each pixel blended from the part of the image it covers.</summary>
    public static PixelImage Resize(PixelImage image, int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        var pixels = new uint[width * height];
        double fx = (double)image.Width / width, fy = (double)image.Height / height;
        int nx = Math.Max(1, (int)Math.Ceiling(fx)), ny = Math.Max(1, (int)Math.Ceiling(fy));
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double r = 0, g = 0, b = 0;
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        double sx = Math.Clamp((x + (i + 0.5) / nx) * fx - 0.5, 0, image.Width - 1), sy = Math.Clamp((y + (j + 0.5) / ny) * fy - 0.5, 0, image.Height - 1);
                        int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(x0 + 1, image.Width - 1), y1 = Math.Min(y0 + 1, image.Height - 1);
                        double ax = sx - x0, ay = sy - y0;
                        Blend(image[x0, y0], (1 - ax) * (1 - ay)); Blend(image[x1, y0], ax * (1 - ay));
                        Blend(image[x0, y1], (1 - ax) * ay); Blend(image[x1, y1], ax * ay);
                    }
                int count = nx * ny;
                pixels[y * width + x] = 0xFF000000 | (uint)((int)Math.Round(r / count) << 16 | (int)Math.Round(g / count) << 8 | (int)Math.Round(b / count));

                void Blend(uint c, double weight) { r += ((c >> 16) & 0xFF) * weight; g += ((c >> 8) & 0xFF) * weight; b += (c & 0xFF) * weight; }
            }
        return new(width, height, pixels);
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
