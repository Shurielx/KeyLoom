using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Keyloom.Models;

public enum MacroBehaviorType { ClickSpam, PressAndHold, Sequence, Script }
public enum TargetInputType { Mouse, Keyboard }
/// <summary>Standard sends system input; Background posts messages to the target window without moving the pointer;
/// QuickSwitch brings the target to the front for each burst of input, then gives focus and the pointer back.</summary>
public enum ExecutionMode { Standard, Background, QuickSwitch }
/// <summary>Auto follows the macro type. Glyph shows one or two characters; Image shows a picture from the icon folder.</summary>
public enum MacroIconKind { Auto, Mouse, Keyboard, Cursor, Glyph, Image }

public class MacroProfile : INotifyPropertyChanged
{
    private bool _isActive;
    private long _executionCount;
    private string _hotkeyStatus = "";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New macro";
    public uint PrimaryVk { get; set; }
    public uint PrimaryModifiers { get; set; }
    public uint FallbackVk { get; set; }
    public uint FallbackModifiers { get; set; }
    public MacroBehaviorType Behavior { get; set; } = MacroBehaviorType.ClickSpam;
    public TargetInputType TargetType { get; set; }
    public string TargetMouseButton { get; set; } = "Left";
    public string TargetKey { get; set; } = "Space";
    public int SpamIntervalMs { get; set; } = 50;
    public int StartDelayMs { get; set; } = 500;
    public int RepeatCount { get; set; }
    /// <summary>
    /// Wait after the pointer moves before a button is pressed. Games read input once per frame, and a press in the
    /// same frame as the jump can land at the old spot or be missed. 0 presses right away.
    /// </summary>
    public int PointerSettleMs { get; set; } = DefaultPointerSettleMs;
    public const int DefaultPointerSettleMs = 20;
    public ObservableCollection<MacroAction> Actions { get; set; } = new();
    public ExecutionMode ExecutionMode { get; set; }
    /// <summary>Quick switch puts your cursor back where it was each time it hands the window back.</summary>
    public bool RestoreCursor { get; set; } = true;
    public WindowTarget? Window { get; set; }
    /// <summary>Repeat and hold modes use PointX, PointY (window client coordinates) instead of the current pointer.</summary>
    public bool UsePoint { get; set; }
    public int PointX { get; set; }
    public int PointY { get; set; }
    /// <summary>When the window size differs from the size at pick time, positions scale proportionally instead of keeping pixel distances.</summary>
    public bool ScaleWithWindow { get; set; }
    /// <summary>Pictures and colors the steps find on the screen.</summary>
    public List<ScreenTarget> Targets { get; set; } = new();
    /// <summary>Parts of the screen split into cells whose numbers the steps read.</summary>
    public List<ScreenGrid> Grids { get; set; } = new();
    /// <summary>A script macro's JavaScript. Experimental.</summary>
    public string Script { get; set; } = "";
    /// <summary>Sequences and scripts look at the screen; repeat and hold do not.</summary>
    [JsonIgnore] public bool LooksAtScreen => Behavior is MacroBehaviorType.Sequence or MacroBehaviorType.Script;
    public MacroIconKind IconKind { get; set; }
    public string IconGlyph { get; set; } = "";
    /// <summary>File name inside the icon folder, never a path.</summary>
    public string IconImage { get; set; } = "";

    [JsonIgnore] public string HotkeyLabel => Services.Shortcut.Format(PrimaryModifiers, PrimaryVk);
    [JsonIgnore] public string BehaviorBadge => Behavior switch
    {
        MacroBehaviorType.ClickSpam => "Repeat input",
        MacroBehaviorType.PressAndHold => "Hold input",
        MacroBehaviorType.Script => "Script",
        _ => "Sequence"
    };
    [JsonIgnore] public string BehaviorSummary => Behavior switch
    {
        MacroBehaviorType.ClickSpam => $"{TargetLabel} every {SpamIntervalMs} ms{PointLabel}",
        MacroBehaviorType.PressAndHold => $"Hold {TargetLabel.ToLowerInvariant()}{PointLabel}",
        MacroBehaviorType.Script => $"Script · {Script.Split('\n').Length} lines · {(RepeatCount == 0 ? "Loop" : RepeatCount == 1 ? "Run once" : $"{RepeatCount} runs")}",
        _ => $"{Actions.Count(a => !a.IsGroupMarker)} steps · {(RepeatCount == 0 ? "Loop" : RepeatCount == 1 ? "Run once" : $"{RepeatCount} runs")}"
    } + ExecutionMode switch { ExecutionMode.Background => " · background", ExecutionMode.QuickSwitch => " · quick switch", _ => "" };
    private string PointLabel => UsePoint && TargetType == TargetInputType.Mouse ? $" at {PointX}, {PointY}" : "";
    [JsonIgnore] public string TargetLabel => TargetType == TargetInputType.Mouse ? $"{TargetMouseButton} click" : TargetKey;
    [JsonIgnore] public string StatusBadgeText => IsActive ? "Running" : "Ready";
    [JsonIgnore] public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; Changed(); Changed(nameof(StatusBadgeText)); }
    }
    [JsonIgnore] public long ExecutionCount
    {
        get => _executionCount;
        set { _executionCount = value; Changed(); }
    }
    [JsonIgnore] public string HotkeyStatus
    {
        get => _hotkeyStatus;
        set { _hotkeyStatus = value; Changed(); }
    }
    private string _runText = "", _usageText = "";
    /// <summary>While running: how long and how many repeats so far, like "0:12 · 34 clicks".</summary>
    [JsonIgnore] public string RunText
    {
        get => _runText;
        set { _runText = value; Changed(); }
    }
    /// <summary>Lifetime use, like "Used 23 times · 1 h 4 min".</summary>
    [JsonIgnore] public string UsageText
    {
        get => _usageText;
        set { _usageText = value; Changed(); }
    }
    /// <summary>What one repeat is called in run counts.</summary>
    [JsonIgnore] public string CycleNoun => Behavior switch
    {
        MacroBehaviorType.ClickSpam => TargetType == TargetInputType.Mouse ? "click" : "press",
        MacroBehaviorType.PressAndHold => "hold",
        _ => "run"
    };
    private bool _isRenaming, _isCapturing;
    /// <summary>The macro list shows a name box instead of the name.</summary>
    [JsonIgnore] public bool IsRenaming
    {
        get => _isRenaming;
        set { _isRenaming = value; Changed(); }
    }
    /// <summary>The macro list waits for a new shortcut.</summary>
    [JsonIgnore] public bool IsCapturing
    {
        get => _isCapturing;
        set { _isCapturing = value; Changed(); }
    }
    /// <summary>Refreshes every binding after settings were changed in place.</summary>
    public void Refresh() => Changed(string.Empty);
    public MacroProfile Snapshot() => new()
    {
        Id = Id, Name = Name, PrimaryVk = PrimaryVk, PrimaryModifiers = PrimaryModifiers,
        FallbackVk = FallbackVk, FallbackModifiers = FallbackModifiers,
        Behavior = Behavior, TargetType = TargetType, TargetMouseButton = TargetMouseButton,
        TargetKey = TargetKey, SpamIntervalMs = SpamIntervalMs, StartDelayMs = StartDelayMs,
        RepeatCount = RepeatCount, PointerSettleMs = PointerSettleMs, Actions = new(Actions.Select(a => a.Clone())),
        ExecutionMode = ExecutionMode, RestoreCursor = RestoreCursor, Window = Window, UsePoint = UsePoint, PointX = PointX, PointY = PointY,
        ScaleWithWindow = ScaleWithWindow, Targets = Targets.Select(t => t.Clone()).ToList(), Grids = Grids.Select(g => g.Clone()).ToList(), Script = Script, IconKind = IconKind, IconGlyph = IconGlyph, IconImage = IconImage
    };
    /// <summary>True when execution needs the target window to be found first.</summary>
    [JsonIgnore] public bool NeedsWindow => ExecutionMode != ExecutionMode.Standard ||
        (UsePoint && !LooksAtScreen && TargetType == TargetInputType.Mouse) ||
        (Behavior == MacroBehaviorType.Sequence && Actions.Any(a => a.ActionType == MacroActionType.MousePosition && a.Space == CoordinateSpace.Window)) ||
        (LooksAtScreen && Targets.Any(t => t.Area?.Space == CoordinateSpace.Window)) ||
        (LooksAtScreen && Grids.Any(g => g.Area.Space == CoordinateSpace.Window));
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
