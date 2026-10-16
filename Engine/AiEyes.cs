using Keyloom.Models;

namespace Keyloom.Engine;

/// <summary>
/// What a running macro asks the AI: it takes the picture (the target window, the whole screen or a part of it) and
/// passes on the answer. The AI is made only when first asked, so macros that never ask need no setup.
/// </summary>
public sealed class AiEyes(Func<IAiVision?> ai, IScreen screen, IWindowSystem windows, IntPtr window)
{
    public static string OffMessage => L.T("AI is off. Turn it on in Settings, under Features, and pick a model.");
    private IAiVision? _vision;
    private IAiVision Vision => _vision ??= ai() ?? throw new InvalidOperationException(OffMessage);

    /// <summary>The target window's inside when the macro has one, otherwise every screen.</summary>
    public (int X, int Y, int Width, int Height) Everything
    {
        get
        {
            if (window == IntPtr.Zero) return screen.Bounds;
            var (width, height) = windows.ClientSize(window);
            return ScreenAreas.Resolve(screen, windows, window, new ScreenArea(0, 0, width, height, CoordinateSpace.Window), L.T("The target window"));
        }
    }
    /// <param name="area">What the AI looks at, in screen pixels; null for <see cref="Everything"/>.</param>
    /// <param name="look">False asks without a picture.</param>
    public string Ask(string question, (int X, int Y, int Width, int Height)? area, bool look, CancellationToken token)
    {
        var vision = Vision;
        PixelImage? picture = null;
        if (look)
        {
            var (x, y, w, h) = area ?? Everything;
            if (w <= 0 || h <= 0) throw new InvalidOperationException(L.T("The part of the screen the AI should look at is not on any screen."));
            picture = screen.Capture(x, y, w, h);
        }
        token.ThrowIfCancellationRequested();
        return vision.Ask(question, picture, token);
    }
    /// <summary>An If that asks: true for yes. An answer that is neither yes nor no counts as no.</summary>
    public bool Holds(MacroAction block, CancellationToken token) =>
        AiVision.YesNo(Ask(block.Question + "\nAnswer only yes or no.", null, true, token)) == true != block.Gone;
}
