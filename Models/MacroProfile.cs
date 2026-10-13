using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Keyloom.Models;

public enum MacroBehaviorType { ClickSpam, PressAndHold, Sequence }
public enum TargetInputType { Mouse, Keyboard }

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

    [JsonIgnore] public string HotkeyLabel => Services.Shortcut.Format(PrimaryModifiers, PrimaryVk);
    [JsonIgnore] public string BehaviorBadge => Behavior switch
    {
        MacroBehaviorType.ClickSpam => "Repeat input",
        MacroBehaviorType.PressAndHold => "Hold input",
        _ => "Sequence"
    };
    [JsonIgnore] public string BehaviorSummary => Behavior switch
    {
        MacroBehaviorType.ClickSpam => $"{TargetLabel} every {SpamIntervalMs} ms",
        MacroBehaviorType.PressAndHold => $"Hold {TargetLabel.ToLowerInvariant()}",
        _ => $"{Actions.Count} steps · {(RepeatCount == 0 ? "Loop" : RepeatCount == 1 ? "Run once" : $"{RepeatCount} runs")}"
    };
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
        RepeatCount = RepeatCount, Actions = new(Actions.Select(a => a.Clone()))
    };
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
