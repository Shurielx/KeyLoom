namespace Keyloom.Models;

/// <summary>What a target looks for on the screen: a small picture, or one color.</summary>
public enum ScreenTargetKind { Picture, Color }

/// <summary>
/// Something a macro finds on the screen, such as a button or a health bar. Steps refer to it by name,
/// so one target can be clicked, waited for and checked from many steps.
/// </summary>
public sealed class ScreenTarget
{
    public const int MaxPictureSide = 600;
    public string Name { get; set; } = "Target";
    public ScreenTargetKind Kind { get; set; }
    /// <summary>The picture as a base64 PNG, kept in the macro file so the macro works on another PC.</summary>
    public string Picture { get; set; } = "";
    /// <summary>"#RRGGBB".</summary>
    public string Color { get; set; } = "#000000";
    /// <summary>How different the screen may look and still count, from 0 (exactly the same) to 100.</summary>
    public int Tolerance { get; set; } = DefaultTolerance;
    public const int DefaultTolerance = 10;
    /// <summary>Where to look. Unset looks on the whole screen, or in the whole target window when the macro has one.</summary>
    public ScreenArea? Area { get; set; }
    public ScreenTarget Clone() => (ScreenTarget)MemberwiseClone();
}

/// <summary>A rectangle on the screen, or in the target window's client area.</summary>
public sealed record ScreenArea(int X, int Y, int Width, int Height, CoordinateSpace Space = CoordinateSpace.Screen);
