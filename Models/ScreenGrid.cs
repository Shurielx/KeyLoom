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
    public ScreenGrid Clone() => (ScreenGrid)MemberwiseClone();
}

/// <summary>Which cell a Click on a cell step picks.</summary>
public enum CellPick { Highest, Lowest, Equals, Empty }
