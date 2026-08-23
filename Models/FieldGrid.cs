namespace TurfTime2.Models;

/// <summary>
/// Pitch formation grid for Field View: 4×4 cells numbered 1–16 (row-major, top-left = 1).
/// </summary>
public static class FieldGrid
{
    public const int Columns = 4;
    public const int Rows = 4;
    public const int CellCount = Columns * Rows;
    public const int MinCell = 1;
    public const int MaxCell = CellCount;

    /// <summary>Returns <paramref name="cell"/> if in 1…16; otherwise null.</summary>
    public static int? Normalize(int? cell)
        => cell is >= MinCell and <= MaxCell ? cell : null;

    /// <summary>Returns <paramref name="cell"/> if in 1…16; otherwise null.</summary>
    public static int? Normalize(int cell)
        => cell is >= MinCell and <= MaxCell ? cell : null;

    public static (int Row, int Column) ToRowColumn(int cell)
    {
        var n = Normalize(cell) ?? MinCell;
        var index = n - 1;
        return (index / Columns, index % Columns);
    }

    public static int FromRowColumn(int row, int column)
    {
        if (row < 0 || row >= Rows || column < 0 || column >= Columns)
            return MinCell;
        return row * Columns + column + 1;
    }
}
