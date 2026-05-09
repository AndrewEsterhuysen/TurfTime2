namespace TurfTime2.Models;

/// <summary>A rotation pair shown in VIEW_C (next bench player IN, next field player OUT).</summary>
public sealed record RotationPair(string BenchPlayerName, string FieldPlayerName);
