using Keyloom.Models;

namespace Keyloom.Engine;

public static class ProfileValidator
{
    public const int MaxActions = 20000;
    public static void Validate(MacroProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 80) throw new ArgumentException("Use a macro name between 1 and 80 characters.");
        if (!Enum.IsDefined(p.Behavior) || !Enum.IsDefined(p.TargetType) || !Enum.IsDefined(p.ExecutionMode)) throw new ArgumentException("Unknown macro mode.");
        if (p.Actions == null || p.Actions.Count > MaxActions) throw new ArgumentException($"A sequence can contain up to {MaxActions:N0} steps.");
        foreach (var action in p.Actions) ValidateAction(action);
        StepGroups.Validate(p.Actions);
        if (p.PrimaryVk > 254 || p.PrimaryModifiers > 15) throw new ArgumentException("Invalid shortcut.");
        if (p.SpamIntervalMs is < 1 or > 3600000) throw new ArgumentException("Repeat interval must be between 1 and 3600000 ms.");
        if (p.StartDelayMs is < 0 or > 3600000) throw new ArgumentException("Start delay must be between 0 and 3600000 ms.");
        if (p.PointerSettleMs is < 0 or > 1000) throw new ArgumentException("Click delay after moving must be between 0 and 1000 ms.");
        if (p.RepeatCount is < 0 or > 1000000) throw new ArgumentException("Repeat count must be between 0 and 1000000. Use 0 to loop.");
        if (p.Behavior == MacroBehaviorType.Sequence)
        {
            if (!p.Actions.Any(a => !a.IsGroupMarker)) throw new ArgumentException("Add at least one sequence step.");
        }
        else if (p.Behavior == MacroBehaviorType.Script)
        {
            p.Script ??= "";
            if (p.Script.Trim().Length == 0) throw new ArgumentException("Write a script, or start from one of the Examples.");
            if (MacroScript.Check(p.Script) is { } problem) throw new ArgumentException("Script " + char.ToLowerInvariant(problem.ToString()[0]) + problem.ToString()[1..]);
        }
        else if (p.TargetType == TargetInputType.Keyboard) KeyResolver.Resolve(p.TargetKey);
        else ValidateButton(p.TargetMouseButton);
        ValidateTarget(p);
        ValidateScreenTargets(p);
        ValidateGrids(p);
        ValidateIcon(p);
    }
    public const int MaxScreenTargets = 100;
    private static void ValidateScreenTargets(MacroProfile p)
    {
        p.Targets ??= new();
        if (p.Targets.Count > MaxScreenTargets) throw new ArgumentException($"A macro can have up to {MaxScreenTargets} targets.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in p.Targets) ValidateScreenTarget(t, names);
        foreach (var a in p.Actions.Where(a => a.ActionType is MacroActionType.ClickTarget or MacroActionType.WaitUntil || a is { ActionType: MacroActionType.GroupStart, Block: BlockKind.If }))
            if (!names.Contains(a.Target ?? ""))
                throw new ArgumentException(string.IsNullOrWhiteSpace(a.Target) ? "A step or an If that looks for a target needs one. Pick or capture it." : $"A step looks for '{a.Target}', but the macro has no target with that name.");
    }
    public const int MaxGrids = 20;
    private static void ValidateGrids(MacroProfile p)
    {
        p.Grids ??= new();
        if (p.Grids.Count > MaxGrids) throw new ArgumentException($"A macro can have up to {MaxGrids} grids.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in p.Grids) ValidateGrid(g, names);
        foreach (var a in p.Actions.Where(a => a.ActionType is MacroActionType.ClickCell or MacroActionType.DragPair))
            if (!names.Contains(a.Grid ?? ""))
                throw new ArgumentException(string.IsNullOrWhiteSpace(a.Grid) ? "A step that reads a grid needs one. Mark the grid on the screen." : $"A step reads '{a.Grid}', but the macro has no grid with that name.");
    }
    public static void ValidateGrid(ScreenGrid g, HashSet<string>? names = null)
    {
        if (g == null || string.IsNullOrWhiteSpace(g.Name) || g.Name.Length > 60) throw new ArgumentException("Give each grid a name of 1 to 60 characters.");
        if (names != null && !names.Add(g.Name)) throw new ArgumentException($"Two grids are called '{g.Name}'. Give each its own name.");
        if (g.Rows < 1 || g.Columns < 1 || (long)g.Rows * g.Columns > ScreenGrid.MaxCells) throw new ArgumentException($"'{g.Name}': a grid has at least 1 row and 1 column, and up to {ScreenGrid.MaxCells} cells.");
        if (g.Area is not { } a || a.Width < g.Columns * 4 || a.Height < g.Rows * 4 || a.Width > 100000 || a.Height > 100000
            || Math.Abs((long)a.X) > 1000000 || Math.Abs((long)a.Y) > 1000000 || a.Space is not (CoordinateSpace.Screen or CoordinateSpace.Window))
            throw new ArgumentException($"'{g.Name}': the grid's area is invalid or too small for its cells. Mark it again.");
    }
    public static void ValidateScreenTarget(ScreenTarget t, HashSet<string>? names = null)
    {
        if (t == null || string.IsNullOrWhiteSpace(t.Name) || t.Name.Length > 60) throw new ArgumentException("Give each target a name of 1 to 60 characters.");
        if (names != null && !names.Add(t.Name)) throw new ArgumentException($"Two targets are called '{t.Name}'. Give each its own name.");
        if (!Enum.IsDefined(t.Kind)) throw new ArgumentException($"'{t.Name}': unknown kind of target.");
        if (t.Tolerance is < 0 or > 100) throw new ArgumentException($"'{t.Name}': tolerance goes from 0 to 100.");
        if (t.Kind == ScreenTargetKind.Color) TargetPicture.ParseColor(t.Color);
        else if (string.IsNullOrEmpty(t.Picture) || t.Picture.Length > 2_000_000) throw new ArgumentException($"'{t.Name}' has no picture. Capture it from the screen.");
        else TargetPicture.Decode(t.Picture);
        if (t.Area is { } a && (a.Width is < 1 or > 100000 || a.Height is < 1 or > 100000 || Math.Abs((long)a.X) > 1000000 || Math.Abs((long)a.Y) > 1000000
            || a.Space is not (CoordinateSpace.Screen or CoordinateSpace.Window)))
            throw new ArgumentException($"'{t.Name}': the area to look in is invalid.");
    }
    private static void ValidateIcon(MacroProfile p)
    {
        p.IconGlyph ??= ""; p.IconImage ??= "";
        if (!Enum.IsDefined(p.IconKind)) throw new ArgumentException("Unknown macro icon.");
        if (new System.Globalization.StringInfo(p.IconGlyph).LengthInTextElements > 2 || p.IconGlyph.Length > 16) throw new ArgumentException("An icon can show up to two characters.");
        if (p.IconImage.Length > 0 && !Services.IconStore.IsValidName(p.IconImage)) throw new ArgumentException("The macro icon image is invalid. Choose it again.");
    }
    private static void ValidateTarget(MacroProfile p)
    {
        if (p.Window is { } w && (w.ProcessName.Length is < 1 or > 260 || w.ClassName.Length > 256 || w.Title.Length > 512 ||
            w.ClientWidth is < 0 or > 100000 || w.ClientHeight is < 0 or > 100000))
            throw new ArgumentException("The saved target window is invalid. Pick the window again.");
        if (p.UsePoint && (p.PointX is < 0 or > 100000 || p.PointY is < 0 or > 100000)) throw new ArgumentException("The chosen point is out of range. Pick it again.");
        if (p.NeedsWindow && p.Window == null) throw new ArgumentException(p.ExecutionMode switch
        {
            ExecutionMode.Background => "Background mode needs a target window. Use Pick point first.",
            ExecutionMode.QuickSwitch => "Quick switch needs a target window. Use Pick point first.",
            _ => "Pick a target window for window-relative positions."
        });
        if (p.ExecutionMode != ExecutionMode.Standard && p.LooksAtScreen &&
            p.Actions.Any(a => a.ActionType == MacroActionType.MousePosition && a.Space == CoordinateSpace.Start))
            throw new ArgumentException("Positions relative to the start point need Standard mode.");
        if (p.ExecutionMode == ExecutionMode.QuickSwitch && p.Behavior == MacroBehaviorType.PressAndHold)
            throw new ArgumentException("Quick switch cannot keep input held while you use another window. Use Repeat, or Standard mode.");
        if (p.ExecutionMode == ExecutionMode.QuickSwitch && p.Behavior == MacroBehaviorType.ClickSpam && p.TargetType == TargetInputType.Mouse && !p.UsePoint)
            throw new ArgumentException("Quick switch clicks at a chosen point. Turn on At a chosen point.");
    }
    public static void ValidateAction(MacroAction a)
    {
        if (a == null || !Enum.IsDefined(a.ActionType)) throw new ArgumentException("Unknown sequence step.");
        switch (a.ActionType)
        {
            case MacroActionType.KeyPress or MacroActionType.KeyDown or MacroActionType.KeyUp: KeyResolver.Resolve(a.KeyName); break;
            case MacroActionType.MouseClick or MacroActionType.MouseDown or MacroActionType.MouseUp: ValidateButton(a.MouseButton); break;
            case MacroActionType.Delay when a.DelayMs is < 1 or > 3600000: throw new ArgumentException("Step delay must be between 1 and 3600000 ms.");
            case MacroActionType.MouseMove when Math.Abs((long)a.X) > 10000 || Math.Abs((long)a.Y) > 10000: throw new ArgumentException("Relative mouse movement is limited to 10000 px per axis.");
            case MacroActionType.MousePosition when !Enum.IsDefined(a.Space) || Math.Abs((long)a.X) > 1000000 || Math.Abs((long)a.Y) > 1000000: throw new ArgumentException("Pointer coordinates are out of range.");
            case MacroActionType.MousePosition when a.GlideMs is < 0 or > 60000: throw new ArgumentException("A cursor can glide for 0 to 60000 ms.");
            case MacroActionType.MouseScroll when a.ScrollAmount is < -100 or > 100 or 0: throw new ArgumentException("Use a scroll amount from -100 to 100, excluding 0.");
            case MacroActionType.Text when string.IsNullOrEmpty(a.Text) || a.Text.Length > 10000: throw new ArgumentException("Text steps must contain between 1 and 10000 characters.");
            case MacroActionType.ClickTarget or MacroActionType.WaitUntil when a.TimeoutMs is < 0 or > 3600000: throw new ArgumentException("Looking for a target can take from 0 to 3600000 ms.");
            case MacroActionType.ClickTarget: ValidateButton(a.MouseButton); break;
            case MacroActionType.ClickCell when !Enum.IsDefined(a.Pick): throw new ArgumentException("Unknown cell to pick.");
            case MacroActionType.ClickCell: ValidateButton(a.MouseButton); break;
            case MacroActionType.DragPair when a.GlideMs is < 0 or > 60000: throw new ArgumentException("A drag can take 0 to 60000 ms.");
            case MacroActionType.DragPair: ValidateButton(a.MouseButton); break;
        }
    }
    private static void ValidateButton(string button)
    {
        if (button is not ("Left" or "Right" or "Middle" or "X1" or "X2")) throw new ArgumentException("Unknown mouse button.");
    }
}
