using System.Text.Json.Serialization;

namespace Keyloom.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MacroActionType { MouseClick, MouseDown, MouseUp, KeyPress, KeyDown, KeyUp, Delay, MouseMove, MouseScroll, Text, MousePosition, GroupStart, GroupEnd }

/// <summary>What a pointer position is measured from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoordinateSpace { Screen, Window, Start }

public class MacroAction
{
    public MacroActionType ActionType { get; set; }
    public string MouseButton { get; set; } = "Left";
    public string KeyName { get; set; } = "Space";
    public int DelayMs { get; set; } = 50;
    public int X { get; set; }
    public int Y { get; set; }
    public CoordinateSpace Space { get; set; }
    public int ScrollAmount { get; set; } = 1;
    public string Text { get; set; } = "";
    // Group settings, kept on the GroupStart marker. Left out of saved files while unset.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public string? Name { get; set; }
    /// <summary>How many times the group plays; 0 and 1 both mean once.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Repeat { get; set; }
    /// <summary>Extra wait between the group's steps, and between its runs.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int GapMs { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Collapsed { get; set; }
    /// <summary>A cursor position step glides there over this time instead of jumping. 0 jumps.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int GlideMs { get; set; }
    /// <summary>How long the step itself takes: a wait, or a cursor gliding to its spot.</summary>
    [JsonIgnore] public int DurationMs => ActionType switch
    {
        MacroActionType.Delay => DelayMs,
        MacroActionType.MousePosition => GlideMs,
        _ => 0
    };
    [JsonIgnore] public bool IsGroupMarker => ActionType is MacroActionType.GroupStart or MacroActionType.GroupEnd;
    [JsonIgnore] public int Runs => Math.Max(1, Repeat);
    [JsonIgnore] public string GroupName => string.IsNullOrWhiteSpace(Name) ? "Group" : Name;
    [JsonIgnore] public string DisplayText => ActionType switch
    {
        MacroActionType.MouseClick => $"Click {MouseButton.ToLowerInvariant()}",
        MacroActionType.MouseDown => $"Hold {MouseButton.ToLowerInvariant()} mouse button",
        MacroActionType.MouseUp => $"Release {MouseButton.ToLowerInvariant()} mouse button",
        MacroActionType.KeyPress => $"Press {KeyName}",
        MacroActionType.KeyDown => $"Hold {KeyName}",
        MacroActionType.KeyUp => $"Release {KeyName}",
        MacroActionType.Delay => $"Wait {DelayMs} ms",
        MacroActionType.MouseMove => $"Move cursor by {X}, {Y} px",
        MacroActionType.MousePosition => (GlideMs > 0 ? "Glide cursor to " : "Cursor to ") + Space switch
        {
            CoordinateSpace.Window => $"{X}, {Y} in window",
            CoordinateSpace.Start => $"start {X:+0;-0;+0}, {Y:+0;-0;+0}",
            _ => $"{X}, {Y}"
        } + (GlideMs > 0 ? $" in {GlideMs} ms" : ""),
        MacroActionType.MouseScroll => $"Scroll {ScrollAmount} notch(es)",
        MacroActionType.GroupStart => $"Group {GroupName}{(Runs > 1 ? $" ×{Runs}" : "")}{(GapMs > 0 ? $", {GapMs} ms between steps" : "")}",
        MacroActionType.GroupEnd => "End of group",
        _ => $"Type: {Text.Replace("\r", " ").Replace("\n", " ")}"
    };
    public MacroAction Clone() => (MacroAction)MemberwiseClone();
}
