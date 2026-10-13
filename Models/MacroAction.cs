using System.Text.Json.Serialization;

namespace Keyloom.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MacroActionType { MouseClick, MouseDown, MouseUp, KeyPress, KeyDown, KeyUp, Delay, MouseMove, MouseScroll, Text }

public class MacroAction
{
    public MacroActionType ActionType { get; set; }
    public string MouseButton { get; set; } = "Left";
    public string KeyName { get; set; } = "Space";
    public int DelayMs { get; set; } = 50;
    public int X { get; set; }
    public int Y { get; set; }
    public int ScrollAmount { get; set; } = 1;
    public string Text { get; set; } = "";
    [JsonIgnore] public string DisplayText => ActionType switch
    {
        MacroActionType.MouseClick => $"Click {MouseButton.ToLowerInvariant()}",
        MacroActionType.MouseDown => $"Hold {MouseButton.ToLowerInvariant()} mouse button",
        MacroActionType.MouseUp => $"Release {MouseButton.ToLowerInvariant()} mouse button",
        MacroActionType.KeyPress => $"Press {KeyName}",
        MacroActionType.KeyDown => $"Hold {KeyName}",
        MacroActionType.KeyUp => $"Release {KeyName}",
        MacroActionType.Delay => $"Wait {DelayMs} ms",
        MacroActionType.MouseMove => $"Move mouse by {X}, {Y} px",
        MacroActionType.MouseScroll => $"Scroll {ScrollAmount} notch(es)",
        _ => $"Type: {Text.Replace("\r", " ").Replace("\n", " ")}"
    };
    public MacroAction Clone() => (MacroAction)MemberwiseClone();
}
