using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Keyloom.Models;

public enum MacroBehaviorType { ClickSpam, PressAndHold, Sequence }
public enum TargetInputType { Mouse, Keyboard }
/// <summary>Standard sends system input; Background posts messages to the target window without moving the pointer.</summary>
public enum ExecutionMode { Standard, Background }

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
    public ObservableCollection<MacroAction> Actions { get; set; } = new();
    public ExecutionMode ExecutionMode { get; set; }
    public WindowTarget? Window { get; set; }
    /// <summary>Repeat and hold modes use PointX, PointY (window client coordinates) instead of the current pointer.</summary>
    public bool UsePoint { get; set; }
    public int PointX { get; set; }
    public int PointY { get; set; }
    /// <summary>When the window size differs from the size at pick time, positions scale proportionally instead of keeping pixel distances.</summary>
    public bool ScaleWithWindow { get; set; }

    [JsonIgnore] public string HotkeyLabel => Services.Shortcut.Format(PrimaryModifiers, PrimaryVk);
    [JsonIgnore] public string BehaviorBadge => Behavior switch
    {
        MacroBehaviorType.ClickSpam => "Repeat input",
        MacroBehaviorType.PressAndHold => "Hold input",
        _ => "Sequence"
    };
    [JsonIgnore] public string BehaviorSummary => Behavior switch
    {
        MacroBehaviorType.ClickSpam => $"{TargetLabel} every {SpamIntervalMs} ms{PointLabel}",
        MacroBehaviorType.PressAndHold => $"Hold {TargetLabel.ToLowerInvariant()}{PointLabel}",
        _ => $"{Actions.Count} steps · {(RepeatCount == 0 ? "Loop" : RepeatCount == 1 ? "Run once" : $"{RepeatCount} runs")}"
    } + (ExecutionMode == ExecutionMode.Background ? " · background" : "");
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
    public MacroProfile Snapshot() => new()
    {
        Id = Id, Name = Name, PrimaryVk = PrimaryVk, PrimaryModifiers = PrimaryModifiers,
        FallbackVk = FallbackVk, FallbackModifiers = FallbackModifiers,
        Behavior = Behavior, TargetType = TargetType, TargetMouseButton = TargetMouseButton,
        TargetKey = TargetKey, SpamIntervalMs = SpamIntervalMs, StartDelayMs = StartDelayMs,
        RepeatCount = RepeatCount, Actions = new(Actions.Select(a => a.Clone())),
        ExecutionMode = ExecutionMode, Window = Window, UsePoint = UsePoint, PointX = PointX, PointY = PointY,
        ScaleWithWindow = ScaleWithWindow
    };
    /// <summary>True when execution needs the target window to be found first.</summary>
    [JsonIgnore] public bool NeedsWindow => ExecutionMode == ExecutionMode.Background ||
        (UsePoint && Behavior != MacroBehaviorType.Sequence && TargetType == TargetInputType.Mouse) ||
        (Behavior == MacroBehaviorType.Sequence && Actions.Any(a => a.ActionType == MacroActionType.MousePosition && a.Space == CoordinateSpace.Window));
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
