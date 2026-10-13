using Keyloom.Models;

namespace Keyloom.Engine;

public static class ProfileValidator
{
    public const int MaxActions = 20000;
    public static void Validate(MacroProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 80) throw new ArgumentException("Use a macro name between 1 and 80 characters.");
        if (!Enum.IsDefined(p.Behavior) || !Enum.IsDefined(p.TargetType)) throw new ArgumentException("Unknown macro mode.");
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
            case MacroActionType.MousePosition when Math.Abs((long)a.X) > 1000000 || Math.Abs((long)a.Y) > 1000000: throw new ArgumentException("Pointer coordinates are out of range.");
            case MacroActionType.MouseScroll when a.ScrollAmount is < -100 or > 100 or 0: throw new ArgumentException("Use a scroll amount from -100 to 100, excluding 0.");
            case MacroActionType.Text when string.IsNullOrEmpty(a.Text) || a.Text.Length > 10000: throw new ArgumentException("Text steps must contain between 1 and 10000 characters.");
        }
    }
    private static void ValidateButton(string button)
    {
        if (button is not ("Left" or "Right" or "Middle" or "X1" or "X2")) throw new ArgumentException("Unknown mouse button.");
    }
}
