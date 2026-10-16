using System.Windows;
using System.Windows.Controls;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Views;

internal static partial class Program
{
    /// <summary>A made-up screen placed where the builder's test monitor is, far off the real desktop.</summary>
    private sealed class PlacedScreen(FakeScreen inner, int left, int top) : IScreen
    {
        public (int X, int Y, int Width, int Height) Bounds => (left, top, inner.Bounds.Width, inner.Bounds.Height);
        public PixelImage Capture(int x, int y, int width, int height) => inner.Capture(x - left, y - top, width, height);
    }

    private static void BuilderTargetTests()
    {
        const int Left = -20000, Top = -20000;
        var fake = new FakeScreen(1280, 800);
        var button = Button(48, 28);
        fake.Paint(700, 300, button);
        var screen = new PlacedScreen(fake, Left, Top);
        var p = Profile(MacroBehaviorType.Sequence);
        p.Targets.Add(new ScreenTarget { Name = "Play", Picture = TargetPicture.Encode(button) });
        p.Targets.Add(new ScreenTarget { Name = "Gone", Kind = ScreenTargetKind.Color, Color = "#FF00FF", Tolerance = 0 });
        p.Actions = new([
            new() { ActionType = MacroActionType.MousePosition, X = Left + 200, Y = Top + 200 },
            new() { ActionType = MacroActionType.ClickTarget, Target = "Play", TimeoutMs = 1000 },
            new() { ActionType = MacroActionType.Delay, DelayMs = 200 },
            new() { ActionType = MacroActionType.MousePosition, X = Left + 400, Y = Top + 500 },
            new() { ActionType = MacroActionType.MouseClick }]);
        var builder = new MacroBuilderWindow("Targets", p.Actions, new RouteSpace(new BuilderWindows(), IntPtr.Zero, p), new Int32Rect(Left, Top, 1280, 800), targets: p.Targets, screen: screen) { ShowActivated = false };
        BuilderResult? result = null;
        builder.Finished += r => result = r;
        builder.Show(); Pump(700);
        var found = (Dictionary<string, Match?>)Field(builder, "_found");
        Check(found["Play"] == new Match(Left + 700, Top + 300, 48, 28) && found["Gone"] == null, "the builder finds its targets on the screen when it opens");
        Invoke(builder, "Pick", new List<int> { 0 }, false); Pump(150);
        var body = (ContentControl)Field(builder, "_panelBody");
        Check(Descendants<ComboBox>((DependencyObject)body.Content).Any(c => (string?)c.SelectedItem == "Play"), "a point's Click on step shows which target it clicks");
        RenderShown((FrameworkElement)builder.Content, "builder-targets-point.png");

        Invoke(builder, "PickTarget", "Play"); Pump(150);
        var texts = Descendants<TextBlock>((DependencyObject)body.Content).Select(t => t.Text).ToList();
        Check(texts.Contains("Target · Play") && texts.Any(t => t.StartsWith("Step 2: Click left on Play")) && ((List<int>)Field(builder, "_picked")).Count == 0,
            "picking a target shows it with the steps that use it");
        RenderShown((FrameworkElement)builder.Content, "builder-target.png");
        var targets = (List<ScreenTarget>)Field(builder, "_targets");
        Invoke(builder, "RenameTarget", targets[0], "Start button");
        var built = (List<MacroAction>)Field(builder, "_steps");
        Check(built[1].Target == "Start button" && found.ContainsKey("Start button"), "renaming a target in the builder renames it in its steps");

        Invoke(builder, "StartSnip", false, null!);
        // Run as a click would, on the window's dispatcher, so the steps after the screen is read come back to it.
        var snip = builder.Dispatcher.InvokeAsync(() => (Task)builder.GetType().GetMethod("FinishSnip", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(builder, [new Int32Rect(Left + 700, Top + 300, 48, 28)])!).Task.Unwrap();
        while (!snip.IsCompleted) Pump(20);
        snip.GetAwaiter().GetResult();
        Check(targets.Count == 3 && targets[2] is { Name: "Picture 1", Kind: ScreenTargetKind.Picture } && TargetPicture.Decode(targets[2].Picture).Pixels.SequenceEqual(button.Pixels)
            && built[^1] is { ActionType: MacroActionType.ClickTarget, Target: "Picture 1" } && (string?)Field(builder, "_pickedTarget") == "Picture 1",
            "a picture dragged on the screen becomes a target with a Click on it");
        Invoke(builder, "Done"); Pump(50);
        Check(result != null && result.Targets.Select(t => t.Name).SequenceEqual(["Start button", "Gone", "Picture 1"]), "Done hands the targets back with the steps");

        var editor = new MacroEditorView(p);
        var host = new Window { Content = editor, Width = 1100, Height = 760, Left = -20000, Top = -20000, ShowActivated = false };
        host.Show(); Pump(150);
        Invoke(editor, "ApplyBuilt", result!);
        var read = (MacroProfile)editor.GetType().GetMethod("ReadForm", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(editor, [p.Snapshot()])!;
        Check(read.Targets.Select(t => t.Name).SequenceEqual(["Start button", "Picture 1"]) && read.Actions.Count(a => a.ActionType == MacroActionType.ClickTarget) == 2,
            "the editor takes the builder's targets and keeps the ones its steps use");
        host.Close();
    }
}
