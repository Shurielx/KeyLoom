namespace Keyloom.Models;

/// <summary>Identifies a top-level window across restarts. Handles change every launch, so matching uses process, class and title.</summary>
public sealed record WindowTarget
{
    public string ProcessName { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>Client size when the point was chosen; used when positions scale with the window.</summary>
    public int ClientWidth { get; init; }
    public int ClientHeight { get; init; }
    public string Label => Title.Length > 0 ? $"{Title} ({ProcessName})" : ProcessName;
}
