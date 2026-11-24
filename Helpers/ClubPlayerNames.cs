using System.Globalization;
using System.Text;

namespace TurfTime2.Helpers;

/// <summary>
/// Name normalising for the club player list (docs/club-players-spec.md §4.1, §7.3):
/// lower-case, trimmed, single spaces, accents removed. Pure (no MAUI types) so it can be unit-tested.
/// </summary>
public static class ClubPlayerNames
{
    public const int MaxNameLength = 80;
    public const int MaxPreferredNameLength = 40;

    /// <summary>"  Zoë   O'Neil " → "zoe o'neil".</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastWasSpace = false;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                lastWasSpace = true;
                continue;
            }
            sb.Append(char.ToLowerInvariant(ch));
            lastWasSpace = false;
        }
        return sb.ToString().TrimEnd().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Stored <c>nameKey</c>: normalised name, plus the normalised preferred name when set
    /// and different ("samuel lee sam"). Used for search.
    /// </summary>
    public static string BuildNameKey(string? name, string? preferredName)
    {
        var n = Normalize(name);
        var p = Normalize(preferredName);
        if (p.Length == 0 || p == n) return n;
        return n.Length == 0 ? p : $"{n} {p}";
    }

    /// <summary>Tidy a display name: trim, collapse inner spaces, cap the length.</summary>
    public static string CleanDisplay(string? value, int maxLength = MaxNameLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var cleaned = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length > maxLength ? cleaned[..maxLength].TrimEnd() : cleaned;
    }

    /// <summary>
    /// Type-to-search: every word typed must start a word in the key ("lee" finds "sam lee",
    /// "sa le" finds "sam lee"). Empty query matches everything.
    /// </summary>
    public static bool Matches(string? nameKey, string? query)
    {
        var q = Normalize(query);
        if (q.Length == 0) return true;
        var words = (nameKey ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var term in q.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!words.Any(w => w.StartsWith(term, StringComparison.Ordinal)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Exact duplicate check (§7.3, exact match only in slice 1): the new name equals an
    /// existing player's name or preferred name once normalised.
    /// </summary>
    public static bool IsExactDuplicate(string? newName, string? existingName, string? existingPreferredName)
    {
        var n = Normalize(newName);
        if (n.Length == 0) return false;
        return n == Normalize(existingName) || n == Normalize(existingPreferredName);
    }
}
