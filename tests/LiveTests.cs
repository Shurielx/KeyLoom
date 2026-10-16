using System.Runtime.InteropServices;
using System.Windows;
using Keyloom.Models;
using Keyloom.Views;

internal static partial class Program
{
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    /// <summary>A real click with the real pointer, as a person would make it.</summary>
    private static void RealClick(FrameworkElement element)
    {
        var center = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        SetCursorPos((int)center.X, (int)center.Y); Pump(120);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); Pump(60); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>
    /// Run with --live: opens real windows on the desktop and saves what the screen shows, with acrylic, DPI scaling and all,
    /// which renders made inside the test never see.
    /// </summary>
    private static void LiveScreens()
    {
        var window = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = 80, Top = 80 };
        window.Show(); Pump(400);
        RealClick((FrameworkElement)window.FindName("FilesButton")); Pump(600);
        Capture(window, "live-files-menu.png");
        ((System.Windows.Controls.ContextMenu)window.FindName("FilesMenu")).IsOpen = false; Pump(300);
        Invoke(window, "Settings_Click", window, new RoutedEventArgs()); Pump(800);
        Capture(window, "live-settings.png");
        Invoke(window, "Back_Click", window, new RoutedEventArgs()); Pump(500);
        var p = Profile(MacroBehaviorType.Sequence);
        p.Actions.Add(new() { ActionType = MacroActionType.MousePosition, X = 200, Y = 150 });
        p.Actions.Add(new() { ActionType = MacroActionType.MouseClick });
        p.Actions.Add(new() { ActionType = MacroActionType.Delay, DelayMs = 200 });
        p.Actions.Add(new() { ActionType = MacroActionType.MousePosition, X = 600, Y = 400 });
        p.Actions.Add(new() { ActionType = MacroActionType.MouseClick });
        // --live-user previews the person's own first sequence, read from their macro file and never saved.
        string own = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Keyloom", "macros.keyloom");
        if (Environment.GetCommandLineArgs().Contains("--live-user") && System.IO.File.Exists(own))
            p = Keyloom.Services.MacroFile.Read(System.IO.File.ReadAllText(own)).First(m => m.Behavior == MacroBehaviorType.Sequence);
        Invoke(window, "OpenEditor", p, false, (Action)(() => { })); Pump(600);
        Capture(window, "live-editor.png");
        var editor = (MacroEditorView)Field(window, "_activeEditor")!;
        Invoke(editor, "Preview_Click", editor, new RoutedEventArgs()); Pump(1200);
        Capture(window, "live-preview.png");
        ((SheetLayer)window.FindName("Sheets")).CloseTop(false); Pump(400);
        RealClick((FrameworkElement)editor.FindName("PreviewButton")); Pump(1200);
        Capture(window, "live-preview-click.png");
        var sheetsLayer = (SheetLayer)window.FindName("Sheets");
        Console.WriteLine($"open={sheetsLayer.IsOpen} top={sheetsLayer.Top?.GetType().Name} layers={sheetsLayer.Children.Count}");
        foreach (System.Windows.Controls.Grid layer in sheetsLayer.Children)
            foreach (FrameworkElement c in layer.Children)
                Console.WriteLine($"  {c.GetType().Name} op={c.Opacity} vis={c.Visibility} size={c.ActualWidth}x{c.ActualHeight} max={c.MaxWidth}x{c.MaxHeight} rt={(c.RenderTransform as System.Windows.Media.ScaleTransform)?.ScaleX}");
        ((SheetLayer)window.FindName("Sheets")).CloseTop(false); Pump(400);
        RealClick((FrameworkElement)editor.FindName("PreviewCard")); Pump(1200);
        Capture(window, "live-route-click.png");
        ((SheetLayer)window.FindName("Sheets")).CloseTop(false); Pump(400);
        window.Close();
    }
    private static void Capture(Window window, string name)
    {
        GetWindowRect(new System.Windows.Interop.WindowInteropHelper(window).Handle, out var r);
        using var bitmap = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using (var g = System.Drawing.Graphics.FromImage(bitmap)) g.CopyFromScreen(r.Left, r.Top, 0, 0, bitmap.Size);
        string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "screenshots"); System.IO.Directory.CreateDirectory(folder);
        bitmap.Save(System.IO.Path.Combine(folder, name), System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine("Saved " + System.IO.Path.Combine(folder, name));
    }
}
