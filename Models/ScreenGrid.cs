namespace Keyloom.Models;

/// <summary>
/// A part of the screen split into equal cells, like a board or an inventory. Keyloom reads the number in each cell,
/// so steps can click the highest one or drag two equal ones together.
/// </summary>
public sealed class ScreenGrid
{
    public const int MaxCells = 400;
    public string Name { get; set; } = "Grid";
    /// <summary>The whole grid, on the screen or in the target window.</summary>
    public ScreenArea Area { get; set; } = new(0, 0, 400, 400);
    public int Rows { get; set; } = 4;
    public int Columns { get; set; } = 4;
    /// <summary>
    /// A target the grid moves with, or null to stay where it was marked. While the macro runs, the grid moves as far as
    /// the target has moved from <see cref="FollowX"/>, <see cref="FollowY"/>: where the target's middle was when the grid
    /// was marked, counted the same way as <see cref="Area"/>.
    /// </summary>
    public string? Follow { get; set; }
    public int FollowX { get; set; }
    public int FollowY { get; set; }
    public ScreenGrid Clone() => (ScreenGrid)MemberwiseClone();
}

/// <summary>Which cell a Click on a cell step picks.</summary>
public enum CellPick { Highest, Lowest, Equals, Empty }
