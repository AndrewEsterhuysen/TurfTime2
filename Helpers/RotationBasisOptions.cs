using TurfTime2.Models;

namespace TurfTime2;

/// <summary>Device-wide preference for how rotation candidates are chosen.</summary>
public static class RotationBasisOptions
{
    public const string PreferenceKey = "game.rotationBasis";
    public const RotationBasis Default = RotationBasis.TimeBased;

    public static event EventHandler? Changed;

    public static RotationBasis Get()
    {
        var raw = Preferences.Get(PreferenceKey, (int)Default);
        return Enum.IsDefined(typeof(RotationBasis), raw)
            ? (RotationBasis)raw
            : Default;
    }

    public static void Set(RotationBasis basis)
    {
        Preferences.Set(PreferenceKey, (int)basis);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static string DisplayName(RotationBasis basis) => basis switch
    {
        RotationBasis.Sequential    => "Sequential",
        RotationBasis.TimeBased     => "Time Based",
        RotationBasis.PositionBased => "Position Based",
        RotationBasis.Manual        => "Manual",
        _                           => "Sequential"
    };

    public static string Description(RotationBasis basis) => basis switch
    {
        RotationBasis.Sequential =>
            "Roster order (current behaviour): next Field after the last who came off, wrapping the list.",
        RotationBasis.TimeBased =>
            "Most field time comes off; least field time comes on. Recomputed after each rotation.",
        RotationBasis.PositionBased =>
            "Cycles occupied players on the Field View grid by row: 1st in each row top→bottom, then 2nd in each row, and so on. Bench uses least field time.",
        RotationBasis.Manual =>
            "You tap who rotates next. No automatic selection. The rotation countdown still runs as a reminder.",
        _ => string.Empty
    };
}
