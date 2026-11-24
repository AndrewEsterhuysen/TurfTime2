using System.Security.Cryptography;
using System.Text;

namespace TurfTime2.Helpers;

/// <summary>Invite and recovery-code helpers shared by the app (and unit tests).</summary>
public static class InviteCodes
{
    /// <summary>Uppercase, strip spaces; keep letters, digits and hyphens (TQAQ-TN2K).</summary>
    public static string Normalize(string? inviteCode)
    {
        if (string.IsNullOrWhiteSpace(inviteCode)) return "";
        var raw = inviteCode.Trim().ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal);
        var chars = raw.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray();
        return new string(chars);
    }

    /// <summary>Alphanumeric-only form (TQAQ-TN2K → TQAQTN2K), used as the invite_codes document id.</summary>
    public static string Compact(string? normalizedCode)
        => new string((normalizedCode ?? "").Where(char.IsLetterOrDigit).ToArray());

    /// <summary>SHA-256 hex (lowercase) of the uppercase-normalised code. Same as TeamDetailsPage.</summary>
    public static string HashAdminCode(string? code)
    {
        var raw = (code ?? "").Trim().ToUpperInvariant();
        if (raw.Length == 0) return "";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
