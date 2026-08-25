using TurfTime2.Models;

namespace TurfTime2.Helpers;

/// <summary>
/// Pure ordering helpers for <see cref="RotationBasis"/> seeding (unit-test friendly).
/// Indices are into the full <paramref name="players"/> roster list.
/// </summary>
public static class RotationOrderBuilder
{
    /// <summary>
    /// Field players sorted by FieldSeconds descending, then roster index ascending.
    /// </summary>
    public static List<int> TimeBasedFieldOrder(IReadOnlyList<Player> players)
        => players
            .Select((p, i) => (p, i))
            .Where(x => x.p.Position == PlayerPosition.Field)
            .OrderByDescending(x => x.p.FieldSeconds)
            .ThenBy(x => x.i)
            .Select(x => x.i)
            .ToList();

    /// <summary>
    /// Bench players sorted by FieldSeconds ascending, then roster index ascending.
    /// </summary>
    public static List<int> TimeBasedBenchOrder(IReadOnlyList<Player> players)
        => players
            .Select((p, i) => (p, i))
            .Where(x => x.p.Position == PlayerPosition.Bench)
            .OrderBy(x => x.p.FieldSeconds)
            .ThenBy(x => x.i)
            .Select(x => x.i)
            .ToList();

    /// <summary>
    /// Occupied-player walk on the 4×4: for rank k = 0,1,2… take the k-th occupied
    /// player in row 0, then row 1, row 2, row 3 (by column among occupied), then k+1…
    /// Players on Field without a valid FieldCell are omitted.
    /// </summary>
    public static List<int> PositionBasedFieldOrder(IReadOnlyList<Player> players)
    {
        // row → list of (column, rosterIndex) sorted by column
        var byRow = new List<(int Col, int Index)>[FieldGrid.Rows];
        for (var r = 0; r < FieldGrid.Rows; r++)
            byRow[r] = [];

        for (var i = 0; i < players.Count; i++)
        {
            var p = players[i];
            if (p.Position != PlayerPosition.Field) continue;
            var cell = FieldGrid.Normalize(p.FieldCell);
            if (cell is null) continue;
            var (row, col) = FieldGrid.ToRowColumn(cell.Value);
            byRow[row].Add((col, i));
        }

        for (var r = 0; r < FieldGrid.Rows; r++)
            byRow[r].Sort((a, b) => a.Col.CompareTo(b.Col));

        var maxRank = 0;
        for (var r = 0; r < FieldGrid.Rows; r++)
            maxRank = Math.Max(maxRank, byRow[r].Count);

        var order = new List<int>();
        for (var rank = 0; rank < maxRank; rank++)
        {
            for (var row = 0; row < FieldGrid.Rows; row++)
            {
                if (rank < byRow[row].Count)
                    order.Add(byRow[row][rank].Index);
            }
        }

        return order;
    }

    /// <summary>
    /// Take <paramref name="count"/> indices from <paramref name="ordered"/> starting at
    /// <paramref name="startOffset"/> (wrapping). Returns the next offset after the take.
    /// </summary>
    public static (List<int> Taken, int NextOffset) TakeWrapping(
        IReadOnlyList<int> ordered, int startOffset, int count)
    {
        var taken = new List<int>();
        if (ordered.Count == 0 || count <= 0)
            return (taken, startOffset);

        var offset = ((startOffset % ordered.Count) + ordered.Count) % ordered.Count;
        var n = Math.Min(count, ordered.Count);
        for (var i = 0; i < n; i++)
        {
            taken.Add(ordered[(offset + i) % ordered.Count]);
        }

        var next = (offset + n) % ordered.Count;
        return (taken, next);
    }
}
