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
    /// <summary>An If that asks the AI this yes-or-no question about the screen instead of looking for a target. Experimental.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public string? Question { get; set; }
    [JsonIgnore] public bool AsksAi => !string.IsNullOrWhiteSpace(Question);
    /// <summary>What an If checks, in words: "Play button is on the screen", or "AI says yes to: Is the shop open?".</summary>
    [JsonIgnore] public string Condition => AsksAi
        ? Gone ? L.T($"AI says no to: {Question}") : L.T($"AI says yes to: {Question}")
        : Gone ? L.T($"{TargetName} is not on the screen") : L.T($"{TargetName} is on the screen");
    // Grids: Click on a cell and Drag a matching pair.
    /// <summary>Name of the macro's <see cref="ScreenGrid"/> the step reads.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public string? Grid { get; set; }
    /// <summary>Click on a cell: which cell.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public CellPick Pick { get; set; }
    /// <summary>Click on a cell with <see cref="CellPick.Equals"/>: the number to look for.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public long CellValue { get; set; }
    [JsonIgnore] public string GridName => string.IsNullOrWhiteSpace(Grid) ? L.T("grid") : Grid;
    [JsonIgnore] public string PickText => Pick switch
    {
        CellPick.Highest => L.T("the highest number"), CellPick.Lowest => L.T("the lowest number"),
        CellPick.Equals => L.T($"the number {CellValue}"), _ => L.T("an empty cell")
    };
    [JsonIgnore] public string TargetName => string.IsNullOrWhiteSpace(Target) ? L.T("target") : Target;
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
        BlockKind.If => L.T("If"), BlockKind.Else => L.T("Else"),
        _ => string.IsNullOrWhiteSpace(Name) ? L.T("Group") : Name
    };
    [JsonIgnore] public string DisplayText => ActionType switch
    {
        MacroActionType.MouseClick => L.T($"Click {ButtonWord}"),
        MacroActionType.MouseDown => L.T($"Hold {ButtonWord} mouse button"),
        MacroActionType.MouseUp => L.T($"Release {ButtonWord} mouse button"),
        MacroActionType.KeyPress => L.T($"Press {KeyName}"),
        MacroActionType.KeyDown => L.T($"Hold {KeyName}"),
        MacroActionType.KeyUp => L.T($"Release {KeyName}"),
        MacroActionType.Delay => L.T($"Wait {DelayMs} ms"),
        MacroActionType.MouseMove => L.T($"Move cursor by {X}, {Y} px"),
        MacroActionType.MousePosition => (GlideMs > 0 ? L.T("Glide cursor to ") : L.T("Cursor to ")) + Space switch
        {
            CoordinateSpace.Window => L.T($"{X}, {Y} in window"),
            CoordinateSpace.Start => L.T($"start {X:+0;-0;+0}, {Y:+0;-0;+0}"),
            _ => $"{X}, {Y}"
        } + (GlideMs > 0 ? L.T($" in {GlideMs} ms") : ""),
        MacroActionType.MouseScroll => L.N(ScrollAmount, "Scroll {0} notch", "Scroll {0} notches"),
        MacroActionType.GroupStart when Block == BlockKind.If => L.T($"If {Condition}"),
        MacroActionType.GroupStart when Block == BlockKind.Else => L.T("Else"),
        MacroActionType.GroupStart => L.T($"Group {GroupName}") + (Runs > 1 ? $" ×{Runs}" : "") + (GapMs > 0 ? L.T($", {GapMs} ms between steps") : ""),
        MacroActionType.GroupEnd => L.T("End of group"),
        MacroActionType.ClickTarget => L.T($"Click {ButtonWord} on {TargetName}"),
        MacroActionType.WaitUntil => Gone ? L.T($"Wait until {TargetName} is gone") : L.T($"Wait until {TargetName} shows"),
        MacroActionType.ClickCell => L.T($"Click {ButtonWord} on {PickText} in {GridName}"),
        MacroActionType.DragPair => L.T($"Drag a matching pair in {GridName}"),
        _ => L.T($"Type: {Text.Replace("\r", " ").Replace("\n", " ")}")
    };
    /// <summary>The mouse button as a word in a sentence: "left", "right", "middle", "back" or "forward".</summary>
    [JsonIgnore] public string ButtonWord => MouseButton switch
    {
        "Left" => L.T("left"), "Right" => L.T("right"), "Middle" => L.T("middle"), "X1" => L.T("back"), "X2" => L.T("forward"), var b => b.ToLowerInvariant()
    };
    public MacroAction Clone() => (MacroAction)MemberwiseClone();
}
