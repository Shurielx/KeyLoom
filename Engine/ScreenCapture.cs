using System.Runtime.InteropServices;

namespace Keyloom.Engine;

/// <summary>Pixels as 0xAARRGGBB, row by row from the top left.</summary>
public sealed class PixelImage(int width, int height, uint[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public uint[] Pixels { get; } = pixels;
    public uint this[int x, int y] => Pixels[y * Width + x];
}

/// <summary>Reads what the screen shows. Tests put in a made-up screen.</summary>
public interface IScreen
{
    /// <summary>The whole desktop across every monitor, in screen pixels.</summary>
    (int X, int Y, int Width, int Height) Bounds { get; }
    /// <summary>Copies a rectangle of the screen. Parts outside every monitor come back black.</summary>
    PixelImage Capture(int x, int y, int width, int height);
}

public sealed class ScreenCapture : IScreen
{
    public static readonly ScreenCapture Instance = new();
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint op);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
    }

    public (int X, int Y, int Width, int Height) Bounds => (GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

    public PixelImage Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return new(0, 0, []);
        IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), bitmap = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            // A negative height gives rows from the top down, the order PixelImage uses.
            var info = new BitmapInfo { Size = Marshal.SizeOf<BitmapInfo>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(screen, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero) throw new InvalidOperationException(L.T("The screen could not be read."));
            old = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, width, height, screen, x, y, 0x00CC0020)) throw new InvalidOperationException(L.T("The screen could not be read."));
            var pixels = new uint[width * height];
            Marshal.Copy(bits, (int[])(object)pixels, 0, pixels.Length);
            for (int i = 0; i < pixels.Length; i++) pixels[i] |= 0xFF000000;
            return new(width, height, pixels);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memory, old);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(memory); ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
