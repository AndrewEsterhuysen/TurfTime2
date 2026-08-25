namespace TurfTime2.Models;

/// <summary>How the app chooses who rotates off the field / on from the bench.</summary>
public enum RotationBasis
{
    /// <summary>Roster-order FIFO after last rotated indices (legacy default).</summary>
    Sequential = 0,

    /// <summary>Highest FieldSeconds off; lowest FieldSeconds on.</summary>
    TimeBased = 1,

    /// <summary>
    /// Occupied players on the 4×4: 1st in each row top→bottom, then 2nd in each row, etc.
    /// Rank is among occupied players in the row (not empty cell numbers).
    /// </summary>
    PositionBased = 2,

    /// <summary>No auto-pick; coach taps who. Countdown reminder still runs.</summary>
    Manual = 3
}
