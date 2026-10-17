using System.Diagnostics;
using System.Windows.Controls;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Services;
using Keyloom.Views;

internal static partial class Program
{
    /// <summary>A made-up screen: a grey gradient with whatever the test paints on it.</summary>
    private sealed class FakeScreen(int width, int height) : IScreen
    {
        public uint[] Pixels { get; } = Enumerable.Range(0, width * height).Select(i => Grey((i % width + i / width) % 200 + 20)).ToArray();
        public int Captures;
        public (int X, int Y, int Width, int Height) Bounds => (0, 0, width, height);
        public PixelImage Capture(int x, int y, int w, int h)
        {
            Interlocked.Increment(ref Captures);
            var pixels = new uint[w * h];
            lock (Pixels) for (int row = 0; row < h; row++) Array.Copy(Pixels, (y + row) * width + x, pixels, row * w, w);
            return new(w, h, pixels);
        }
        public void Paint(int x, int y, PixelImage image)
        {
            lock (Pixels) for (int row = 0; row < image.Height; row++) Array.Copy(image.Pixels, row * image.Width, Pixels, (y + row) * width + x, image.Width);
        }
        public void Fill(int x, int y, int w, int h, uint color)
        {
            lock (Pixels) for (int row = y; row < y + h; row++) Array.Fill(Pixels, color, row * width + x, w);
        }
        public PixelImage Snapshot() => Capture(0, 0, width, height);
    }
    private static uint Grey(int v) => 0xFF000000 | (uint)(v << 16 | v << 8 | v);
    /// <summary>A small button-like picture: a frame with a cross and a few colors, so it is unlike its surroundings.</summary>
    private static PixelImage Button(int w = 24, int h = 14)
    {
        var pixels = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                pixels[y * w + x] = x == 0 || y == 0 || x == w - 1 || y == h - 1 ? 0xFF1060C0 : x == y || x == w - 1 - y ? 0xFFFFFFFF : 0xFF30A050 + (uint)(x * 3);
        return new(w, h, pixels);
    }

    private static async Task VisionEngineTests()
    {
        var screen = new FakeScreen(640, 400);
        var button = Button();
        screen.Paint(300, 200, button);
        var found = TargetFinder.FindPicture(screen.Snapshot(), button, 10);
        Check(found == new Match(300, 200, 24, 14) && found.Value.Center == (312, 207), "a picture is found where it is on the screen");
        var faded = new PixelImage(button.Width, button.Height, button.Pixels.Select(c => c - 0x00080808).ToArray());
        screen.Paint(300, 200, faded);
        Check(TargetFinder.FindPicture(screen.Snapshot(), button, 10)?.X == 300 && TargetFinder.FindPicture(screen.Snapshot(), button, 0) == null,
            "tolerance lets a slightly different picture count, and 0 needs it exact");
        Check(TargetFinder.FindPicture(screen.Snapshot(), Button(30, 20), 10) == null, "a picture that is not there is not found");
        var big = new FakeScreen(1920, 1080);
        big.Paint(1700, 900, button);
        var shot = big.Snapshot();
        var clock = Stopwatch.StartNew();
        var far = TargetFinder.FindPicture(shot, button, 10);
        clock.Stop();
        Check(far?.X == 1700 && clock.ElapsedMilliseconds < 400, $"a full HD screen is searched quickly ({clock.ElapsedMilliseconds} ms)");

        // A game camera that zooms draws a picture a little smaller or larger. It is still found, with how much it changed.
        var title = DrawTitle(1);
        foreach (var (scale, x, y) in new[] { (0.9, 1500, 820), (1.1, 240, 130) })
        {
            var zoomed = new FakeScreen(1920, 1080);
            zoomed.Paint(x, y, DrawTitle(scale));
            var zoomedShot = zoomed.Snapshot();
            clock.Restart();
            var sized = TargetFinder.FindPictureAnySize(zoomedShot, title, 10);
            clock.Stop();
            Check(TargetFinder.FindPicture(zoomedShot, title, 10) == null && sized is { } z && Math.Abs(z.X - x) <= 2 && Math.Abs(z.Y - y) <= 2
                && Math.Abs(z.Scale - scale) <= 0.02 && Math.Abs(z.Width - title.Width * scale) <= 3 && clock.ElapsedMilliseconds < 400,
                $"a picture drawn at {scale * 100:0} percent is found on a full HD screen, with its size, quickly ({sized}, {clock.ElapsedMilliseconds} ms)");
        }
        // At 4K and 150 percent display scale, the way many players run, the title is larger and so is the screen.
        var large = new FakeScreen(3840, 2160);
        large.Paint(2600, 1500, DrawTitle(1.5 * 0.95));
        var largeShot = large.Snapshot();
        clock.Restart();
        var zoomedLarge = TargetFinder.FindPictureAnySize(largeShot, DrawTitle(1.5), 10);
        clock.Stop();
        Check(zoomedLarge is { } l && Math.Abs(l.X - 2600) <= 2 && Math.Abs(l.Scale - 0.95) <= 0.02 && clock.ElapsedMilliseconds < 800,
            $"a larger picture zoomed out a little is found on a 4K screen quickly ({zoomedLarge}, {clock.ElapsedMilliseconds} ms)");
        clock.Restart();
        var nowhere = TargetFinder.FindPictureAnySize(shot, title, 10);
        clock.Stop();
        Check(nowhere == null && clock.ElapsedMilliseconds < 600, $"a picture at no size is not found, and looking at every size stays quick ({clock.ElapsedMilliseconds} ms)");
        Check(TargetFinder.FindPictureAnySize(screen.Snapshot(), button, 10) == new Match(300, 200, 24, 14), "a picture at its own size is found as it was, at scale 1");

        screen.Fill(100, 50, 40, 20, 0xFFFF8800);
        var orange = TargetFinder.FindColor(screen.Snapshot(), 0xFFFF8800, 4);
        Check(orange?.Center == (119, 59) && orange.Value.Width == 40 && orange.Value.Height == 20, "a color is found, and clicked in the middle of its patch");
        Check(TargetFinder.FindColor(screen.Snapshot(), 0xFFFF0000, 4) == null && TargetFinder.FindColor(screen.Snapshot(), 0xFFF88A04, 4) != null,
            "a color matches within its tolerance only");
        Check(TargetPicture.ParseColor("#ff8800") == 0xFFFF8800 && TargetPicture.ColorText(0xFFFF8800) == "#FF8800" && Fails(() => TargetPicture.ParseColor("orange"), "not a color"),
            "colors read and write as #RRGGBB");
        var decoded = TargetPicture.Decode(TargetPicture.Encode(button));
        Check(decoded.Width == 24 && decoded.Pixels.SequenceEqual(button.Pixels), "a target picture survives being saved as PNG text");

        // Click on target clicks the middle of where the picture is; Wait until holds the macro until the screen changes.
        screen = new FakeScreen(640, 400);
        var driver = new FakeDriver();
        await using var executor = new MacroExecutor(driver, screen: screen);
        string failure = "";
        executor.ExecutionFailed += m => failure = m;
        var p = Profile(MacroBehaviorType.Sequence); p.RepeatCount = 1;
        p.Targets.Add(new ScreenTarget { Name = "Play", Picture = TargetPicture.Encode(button) });
        p.Targets.Add(new ScreenTarget { Name = "Ready", Kind = ScreenTargetKind.Color, Color = "#00FF00", Tolerance = 2 });
        p.Actions.Add(new() { ActionType = MacroActionType.WaitUntil, Target = "Ready" });
        p.Actions.Add(new() { ActionType = MacroActionType.ClickTarget, Target = "Play", MouseButton = "Right", TimeoutMs = 1000 });
        await executor.ToggleAsync(p);
        await Task.Delay(250);
        Check(executor.ActiveProfile != null && driver.Events.IsEmpty, "Wait until holds the macro while the target is not on the screen");
        screen.Fill(10, 10, 6, 6, 0xFF00FF00);
        screen.Paint(500, 300, button);
        await Until(() => executor.ActiveProfile == null);
        Check(driver.Events.SequenceEqual(["position:512,307", "MRight:down", "MRight:up"]) && failure == "", "once it shows, Click on target clicks the middle of the picture");

        driver.Events.Clear();
        screen = new FakeScreen(640, 400);
        await using var missing = new MacroExecutor(driver, screen: screen);
        missing.ExecutionFailed += m => failure = m;
        p.Actions.Clear();
        p.Actions.Add(new() { ActionType = MacroActionType.ClickTarget, Target = "Play", TimeoutMs = 120 });
        p.Actions.Add(new() { ActionType = MacroActionType.KeyPress, KeyName = "E" });
        clock.Restart();
        await missing.ToggleAsync(p); await Until(() => missing.ActiveProfile == null);
        Check(clock.ElapsedMilliseconds >= 110 && driver.Events.All(e => e.StartsWith('K')) && driver.Events.Count == 2 && failure == "",
            "a target that does not show in time is skipped and the macro goes on");
        p.Actions[0].StopIfMissing = true;
        driver.Events.Clear();
        await missing.ToggleAsync(p); await Until(() => missing.ActiveProfile == null);
        Check(driver.Events.IsEmpty && failure.Contains("'Play' was not found"), "a step set to stop ends the macro and says what was missing");
        p.Actions.Clear();
        p.Actions.Add(new() { ActionType = MacroActionType.WaitUntil, Target = "Play", Gone = true, TimeoutMs = 2000 });
        screen.Paint(40, 40, button);
        failure = "";
        await missing.ToggleAsync(p);
        await Task.Delay(150);
        screen.Fill(40, 40, 24, 14, Grey(50));
        await Until(() => missing.ActiveProfile == null);
        Check(failure == "", "Wait until gone goes on once the target is gone");

        // The steps and targets in the macro file.
        p.Actions.Add(new() { ActionType = MacroActionType.ClickTarget, Target = "Ready", MouseButton = "Middle", TimeoutMs = 500, StopIfMissing = true });
        p.Targets[1].Area = new ScreenArea(5, 6, 70, 80, CoordinateSpace.Screen);
        string json = MacroFile.Write([p]);
        var back = MacroFile.Read(json).Single();
        Check(json.Contains("\"do\": \"waitUntil\"") && json.Contains("\"until\": \"gone\"") && json.Contains("\"ifTimeUp\": \"stop\"") && json.Contains("\"color\": \"#00FF00\""),
            "targets and their steps are written in plain words");
        Check(back.Targets.Count == 2 && back.Targets[0].Picture == p.Targets[0].Picture && back.Targets[1].Area == p.Targets[1].Area && back.Targets[1].Tolerance == 2
            && back.Actions[1] is { ActionType: MacroActionType.ClickTarget, Target: "Ready", MouseButton: "Middle", TimeoutMs: 500, StopIfMissing: true }
            && back.Actions[0] is { Gone: true, TimeoutMs: 2000 }, "targets and their steps read back the same");
        Check(Fails(() => MacroFile.Read(json.Replace("\"target\": \"Ready\"", "\"target\": \"Nope\"")), "no target with that name"), "a step looking for a target the macro lacks is refused");
        Check(Fails(() => MacroFile.Read(json.Replace("\"until\": \"gone\"", "\"until\": \"later\"")), "unknown \"until\""), "an unknown until says what to use");
        Check(Fails(() => MacroFile.Read(json.Replace("\"color\": \"#00FF00\"", "\"color\": \"green\"")), "not a color"), "a color has to be #RRGGBB");
    }

    private static void VisionUiTests()
    {
        var p = Profile(MacroBehaviorType.Sequence);
        p.Targets.Add(new ScreenTarget { Name = "Play", Picture = TargetPicture.Encode(Button()) });
        p.Targets.Add(new ScreenTarget { Name = "Unused", Kind = ScreenTargetKind.Color, Color = "#123456" });
        p.Actions.Add(new() { ActionType = MacroActionType.ClickTarget, Target = "Play", TimeoutMs = 700 });
        var editor = new MacroEditorView(p);
        var host = new System.Windows.Window { Content = editor, Width = 1100, Height = 760, Left = -20000, Top = -20000, ShowActivated = false };
        host.Show(); Pump(200);
        var list = (ListBox)editor.FindName("ActionsListBox");
        list.SelectedIndex = 0; Pump(100);
        var options = (StackPanel)editor.FindName("TargetOptions");
        var card = (Border)editor.FindName("TargetCard");
        var info = (TextBlock)editor.FindName("TargetInfoText");
        Check(options.IsVisible && card.IsVisible && info.Text.Contains("24 × 14") && ((TextBox)editor.FindName("TimeoutTextBox")).Text == "700"
            && !((StackPanel)editor.FindName("UntilPanel")).IsVisible, "a Click on target step shows its picture, its name and how long it looks");
        var name = (TextBox)editor.FindName("TargetNameTextBox");
        name.Text = "Start button";
        Invoke(editor, "RenameTarget");
        Check(((Keyloom.Models.MacroAction)list.Items[0]).Target == "Start button", "renaming a target renames it in the steps that look for it");
        ((ComboBox)editor.FindName("StepTypeBox")).SelectedValue = MacroActionType.WaitUntil; Pump(50);
        Check(((StackPanel)editor.FindName("UntilPanel")).IsVisible && ((TextBlock)editor.FindName("TimeoutLabel")).Text == "Give up after",
            "turning the step into Wait until asks whether it waits for it to show or to go");
        var saved = p.Snapshot();
        var read = (MacroProfile)editor.GetType().GetMethod("ReadForm", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(editor, [saved])!;
        Check(read.Targets.Select(t => t.Name).SequenceEqual(["Start button"]) && read.Actions[0] is { ActionType: MacroActionType.WaitUntil, Target: "Start button" },
            "saving keeps the targets the steps use and drops the rest");
        Render(editor, 1100, 760, "vision-step.png");
        Invoke(editor, "Copy", new List<int> { 0 });
        var other = new MacroEditorView(Profile(MacroBehaviorType.Sequence));
        host.Content = other; Pump(150);
        Invoke(other, "Paste");
        var pasted = (MacroProfile)other.GetType().GetMethod("ReadForm", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(other, [Profile(MacroBehaviorType.Sequence)])!;
        Check(pasted.Targets.Single().Name == "Start button" && pasted.Actions.Single().Target == "Start button", "steps pasted into another macro bring their target along");
        host.Close();
    }
}
