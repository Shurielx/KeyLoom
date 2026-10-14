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
        if (p.PrimaryVk > 254 || p.PrimaryModifiers > 15) throw new ArgumentException("Invalid shortcut.");
        if (p.SpamIntervalMs is < 1 or > 3600000) throw new ArgumentException("Repeat interval must be between 1 and 3600000 ms.");
        if (p.StartDelayMs is < 0 or > 3600000) throw new ArgumentException("Start delay must be between 0 and 3600000 ms.");
        if (p.RepeatCount is < 0 or > 1000000) throw new ArgumentException("Repeat count must be between 0 and 1000000. Use 0 to loop.");
        if (p.Behavior == MacroBehaviorType.Sequence)
        {
            if (p.Actions.Count < 1) throw new ArgumentException("Add at least one sequence step.");
        }
        else if (p.TargetType == TargetInputType.Keyboard) KeyResolver.Resolve(p.TargetKey);
        else ValidateButton(p.TargetMouseButton);
        ValidateTarget(p);
        ValidateIcon(p);
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
        if (p.ExecutionMode != ExecutionMode.Standard && p.Behavior == MacroBehaviorType.Sequence &&
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
            case MacroActionType.MouseScroll when a.ScrollAmount is < -100 or > 100 or 0: throw new ArgumentException("Use a scroll amount from -100 to 100, excluding 0.");
            case MacroActionType.Text when string.IsNullOrEmpty(a.Text) || a.Text.Length > 10000: throw new ArgumentException("Text steps must contain between 1 and 10000 characters.");
        }
    }
    private static void ValidateButton(string button)
    {
        if (button is not ("Left" or "Right" or "Middle" or "X1" or "X2")) throw new ArgumentException("Unknown mouse button.");
    }
}
