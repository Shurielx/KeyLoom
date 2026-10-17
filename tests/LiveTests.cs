using System.Runtime.InteropServices;
using System.Windows;
using Keyloom.Models;
using Keyloom.Services;
using Keyloom.Views;

internal static partial class Program
{
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    /// <summary>A real click with the real pointer, as a person would make it.</summary>
    private static void RealClick(FrameworkElement element, double across = 0.5)
    {
        var center = element.PointToScreen(new Point(element.ActualWidth * across, element.ActualHeight / 2));
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
        LiveVision(window);
        LiveScript(window);
        LiveBuilder();
        window.Close();
    }
    /// <summary>
    /// A Click on target step in the real editor, then the screen freeze for New color, clicked with the real mouse.
    /// Only Keyloom's own window is saved; the frozen screen shows the person's desktop, so it is never captured.
    /// </summary>
    private static void LiveVision(MainWindow window)
    {
        var p = Profile(MacroBehaviorType.Sequence);
        p.Targets.Add(new ScreenTarget { Name = "Play button", Picture = Keyloom.Engine.TargetPicture.Encode(Button(48, 28)) });
        p.Actions.Add(new() { ActionType = MacroActionType.WaitUntil, Target = "Play button", TimeoutMs = 5000 });
        p.Actions.Add(new() { ActionType = MacroActionType.ClickTarget, Target = "Play button", TimeoutMs = 1000 });
        Invoke(window, "Back_Click", window, new RoutedEventArgs()); Pump(300);
        Invoke(window, "OpenEditor", p, false, (Action)(() => { })); Pump(600);
        var editor = (MacroEditorView)Field(window, "_activeEditor")!;
        ((System.Windows.Controls.ListBox)editor.FindName("ActionsListBox")).SelectedIndex = 1; Pump(400);
        Capture(window, "live-vision-step.png");
        var snip = ScreenSnipWindow.TakeAsync(true);
        Pump(600);
        var snipWindow = Application.Current.Windows.OfType<ScreenSnipWindow>().Single();
        GetWindowRect(new System.Windows.Interop.WindowInteropHelper(snipWindow).Handle, out var r);
        SetCursorPos(r.Left + 40, r.Top + 40); Pump(150);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); Pump(60); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        var deadline = DateTime.Now.AddSeconds(3);
        while (!snip.IsCompleted && DateTime.Now < deadline) Pump(50);
        var picked = snip.IsCompleted ? snip.Result : null;
        var monitor = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(r.Left + 40, r.Top + 40)).Bounds;
        Console.WriteLine($"snip covers {r.Right - r.Left}x{r.Bottom - r.Top} of monitor {monitor.Width}x{monitor.Height}; picked {(picked == null ? "nothing" : $"color at {picked.Bounds.X},{picked.Bounds.Y}")}; open windows {Application.Current.Windows.OfType<ScreenSnipWindow>().Count()}");
        editor.Cancel(); Pump(300);

        // An If with an Else, picked in the real editor.
        var q = Profile(MacroBehaviorType.Sequence);
        q.Targets.Add(new ScreenTarget { Name = "Reward chest", Kind = ScreenTargetKind.Color, Color = "#FFCC00" });
        q.Actions = new([
            new() { ActionType = MacroActionType.KeyPress, KeyName = "E" },
            new() { ActionType = MacroActionType.GroupStart, Block = BlockKind.If, Target = "Reward chest", TimeoutMs = 500 },
            new() { ActionType = MacroActionType.ClickTarget, Target = "Reward chest", TimeoutMs = 1000 },
            new() { ActionType = MacroActionType.GroupEnd },
            new() { ActionType = MacroActionType.GroupStart, Block = BlockKind.Else },
            new() { ActionType = MacroActionType.KeyPress, KeyName = "R" },
            new() { ActionType = MacroActionType.GroupEnd }]);
        Invoke(window, "OpenEditor", q, false, (Action)(() => { })); Pump(600);
        editor = (MacroEditorView)Field(window, "_activeEditor")!;
        var steps = (System.Windows.Controls.ListBox)editor.FindName("ActionsListBox");
        RealClick((FrameworkElement)steps.ItemContainerGenerator.ContainerFromIndex(1), 0.2); Pump(500);
        Capture(window, "live-if.png");
        RealClick((FrameworkElement)steps.ItemContainerGenerator.ContainerFromIndex(4), 0.2); Pump(500);
        Capture(window, "live-else.png");
        RealClick((FrameworkElement)steps.ItemContainerGenerator.ContainerFromIndex(2), 0.25); Pump(500);
        Capture(window, "live-if-inner-step.png");
        editor.Cancel(); Pump(300);
    }
    /// <summary>Turn into script with real clicks, asked in a sheet, then the script's settings beside it. Only Keyloom's window is saved.</summary>
    private static void LiveScript(MainWindow window)
    {
        bool scripts = AppSettings.Current.Scripts;
        try
        {
            AppSettings.Current.Scripts = true;
            var p = Profile(MacroBehaviorType.Sequence);
            p.Targets.Add(new ScreenTarget { Name = "Play", Picture = Keyloom.Engine.TargetPicture.Encode(Button(48, 28)) });
            p.Grids.Add(new ScreenGrid { Name = "Board", Area = new(100, 100, 400, 400), Rows = 4, Columns = 4 });
            p.Actions = new([
                new() { ActionType = MacroActionType.GroupStart, Name = "Merge", Repeat = 5, GapMs = 80 },
                new() { ActionType = MacroActionType.DragPair, Grid = "Board", GlideMs = 120 },
                new() { ActionType = MacroActionType.GroupEnd },
                new() { ActionType = MacroActionType.GroupStart, Block = BlockKind.If, Target = "Play", TimeoutMs = 500 },
                new() { ActionType = MacroActionType.ClickTarget, Target = "Play" },
                new() { ActionType = MacroActionType.GroupEnd },
                new() { ActionType = MacroActionType.Delay, DelayMs = 300 }]);
            Invoke(window, "Back_Click", window, new RoutedEventArgs()); Pump(300);
            Invoke(window, "OpenEditor", p, false, (Action)(() => { })); Pump(600);
            var editor = (MacroEditorView)Field(window, "_activeEditor")!;
            RealClick((FrameworkElement)editor.FindName("ToScriptButton")); Pump(700);
            Capture(window, "live-to-script-ask.png");
            var sheets = (SheetLayer)window.FindName("Sheets");
            var confirm = Descendants<System.Windows.Controls.Button>(sheets).FirstOrDefault(b => b.Content as string == "Turn into script");
            if (confirm != null) { RealClick(confirm); Pump(800); }
            var box = (ICSharpCode.AvalonEdit.TextEditor)editor.FindName("ScriptBox");
            Console.WriteLine($"turned into a script: {((System.Windows.Controls.RadioButton)editor.FindName("ScriptRadio")).IsChecked == true}, {box.Document.LineCount} lines");
            Capture(window, "live-to-script.png");
            box.Document.Insert(0, "const SPEED = 80 // setting: How fast\nconst SELL = false // setting: Sell the rest\nconst MODE = \"merge\" // setting: Mode, merge | sell\n\n");
            Invoke(editor, "CheckScript"); Pump(400);
            Capture(window, "live-script-settings.png");
            editor.Cancel(); Pump(300);
        }
        finally { AppSettings.Current.Scripts = scripts; }
    }
    /// <summary>
    /// The builder on the real screen finds a picture shown in a Keyloom test window. Nothing is captured: the builder covers
    /// the whole desktop, so only where it found the picture is printed.
    /// </summary>
    private static void LiveBuilder()
    {
        var picture = Button(48, 28);
        var shown = new Window
        {
            Title = "Keyloom live target", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowActivated = false,
            Left = 300, Top = 300, SizeToContent = SizeToContent.WidthAndHeight,
            Content = new System.Windows.Controls.Image { Source = ScreenSnipWindow.ToBitmap(picture), Stretch = System.Windows.Media.Stretch.None }
        };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode((DependencyObject)shown.Content, System.Windows.Media.BitmapScalingMode.NearestNeighbor);
        shown.Show(); Pump(200);
        var image = (System.Windows.Controls.Image)shown.Content;
        // One picture pixel per screen pixel, whatever the scale, so the screen shows exactly the picture.
        double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(shown).DpiScaleX;
        image.Stretch = System.Windows.Media.Stretch.Fill; image.Width = picture.Width / dpi; image.Height = picture.Height / dpi;
        shown.UseLayoutRounding = true; shown.SnapsToDevicePixels = true;
        Pump(400);
        var at = image.PointToScreen(new Point(0, 0));
        var p = Profile(MacroBehaviorType.Sequence);
        p.Targets.Add(new ScreenTarget { Name = "Live", Picture = Keyloom.Engine.TargetPicture.Encode(picture) });
        p.Actions.Add(new() { ActionType = MacroActionType.ClickTarget, Target = "Live" });
        var monitor = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)at.X, (int)at.Y)).Bounds;
        var builder = new MacroBuilderWindow("Live", p.Actions, new Keyloom.Engine.RouteSpace(Keyloom.Engine.WindowSystem.Instance, IntPtr.Zero, p),
            new Int32Rect(monitor.X, monitor.Y, monitor.Width, monitor.Height), targets: p.Targets) { ShowActivated = false };
        builder.Show(); Pump(1500);
        var found = ((Dictionary<string, Keyloom.Engine.Match?>)Field(builder, "_found")).GetValueOrDefault("Live");
        Console.WriteLine($"builder found the live picture at {found?.X},{found?.Y} (shown at {(int)at.X},{(int)at.Y}); builder opacity {builder.Opacity}");
        builder.Close(); shown.Close(); Pump(200);
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
