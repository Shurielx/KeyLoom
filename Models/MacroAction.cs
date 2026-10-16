using System.Text.Json.Serialization;

namespace Keyloom.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MacroActionType { MouseClick, MouseDown, MouseUp, KeyPress, KeyDown, KeyUp, Delay, MouseMove, MouseScroll, Text, MousePosition, GroupStart, GroupEnd, ClickTarget, WaitUntil, ClickCell, DragPair }

/// <summary>What a pointer position is measured from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoordinateSpace { Screen, Window, Start }

/// <summary>What a GroupStart opens: a plain group, an If that plays only when its target is (or is not) on the screen,
/// or the Else right after an If, which plays when the If did not.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BlockKind { Group, If, Else }

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
    /// <summary>On a GroupStart: a plain group, an If or an Else. An If uses <see cref="Target"/>, <see cref="Gone"/> and <see cref="TimeoutMs"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public BlockKind Block { get; set; }
    /// <summary>A cursor position step glides there over this time instead of jumping. 0 jumps.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int GlideMs { get; set; }
    // Screen targets: Click on target and Wait until.
    /// <summary>Name of the macro's <see cref="ScreenTarget"/> the step looks for.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public string? Target { get; set; }
    /// <summary>Click on target: how long to keep looking, 0 looks once. Wait until: when to give up, 0 waits for as long as it takes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int TimeoutMs { get; set; }
    /// <summary>Wait until the target is gone, instead of until it shows up.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Gone { get; set; }
    /// <summary>Stop the macro when the target is not found in time, instead of going on with the next step.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool StopIfMissing { get; set; }
    /// <summary>What an If checks, in words: "Play button is on the screen".</summary>
    [JsonIgnore] public string Condition => $"{TargetName} {(Gone ? "is not on the screen" : "is on the screen")}";
    // Grids: Click on a cell and Drag a matching pair.
    /// <summary>Name of the macro's <see cref="ScreenGrid"/> the step reads.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public string? Grid { get; set; }
    /// <summary>Click on a cell: which cell.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public CellPick Pick { get; set; }
    /// <summary>Click on a cell with <see cref="CellPick.Equals"/>: the number to look for.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long CellValue { get; set; }
    [JsonIgnore] public string GridName => string.IsNullOrWhiteSpace(Grid) ? "grid" : Grid;
    [JsonIgnore] public string PickText => Pick switch
    {
        CellPick.Highest => "the highest number", CellPick.Lowest => "the lowest number",
        CellPick.Equals => $"the number {CellValue}", _ => "an empty cell"
    };
    [JsonIgnore] public string TargetName => string.IsNullOrWhiteSpace(Target) ? "target" : Target;
    /// <summary>How long the step itself takes: a wait, or a cursor gliding to its spot.</summary>
    [JsonIgnore] public int DurationMs => ActionType switch
    {
        MacroActionType.Delay => DelayMs,
        MacroActionType.MousePosition => GlideMs,
        _ => 0
    };
    [JsonIgnore] public bool IsGroupMarker => ActionType is MacroActionType.GroupStart or MacroActionType.GroupEnd;
    [JsonIgnore] public int Runs => Math.Max(1, Repeat);
    [JsonIgnore] public string GroupName => Block switch
    {
        BlockKind.If => "If", BlockKind.Else => "Else",
        _ => string.IsNullOrWhiteSpace(Name) ? "Group" : Name
    };
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
        MacroActionType.GroupStart when Block == BlockKind.If => $"If {Condition}",
        MacroActionType.GroupStart when Block == BlockKind.Else => "Else",
        MacroActionType.GroupStart => $"Group {GroupName}{(Runs > 1 ? $" ×{Runs}" : "")}{(GapMs > 0 ? $", {GapMs} ms between steps" : "")}",
        MacroActionType.GroupEnd => "End of group",
        MacroActionType.ClickTarget => $"Click {MouseButton.ToLowerInvariant()} on {TargetName}",
        MacroActionType.WaitUntil => $"Wait until {TargetName} {(Gone ? "is gone" : "shows")}",
        MacroActionType.ClickCell => $"Click {MouseButton.ToLowerInvariant()} on {PickText} in {GridName}",
        MacroActionType.DragPair => $"Drag a matching pair in {GridName}",
        _ => $"Type: {Text.Replace("\r", " ").Replace("\n", " ")}"
    };
    public MacroAction Clone() => (MacroAction)MemberwiseClone();
}
