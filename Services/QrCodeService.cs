using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TurfTime2.Helpers;
using TurfTime2.Models;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace TurfTime2.Services;

public static class QrCodeService
{
    private const string TeamModeKey = "team_mode";
    private const string TeamIdKey = "team_id";
    private const string TeamNameKey = "team_name";
    private const string UserRoleKey = "user_role";
    private static int _importCounter;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static TeamShareData CreateFromCurrentTeam(string teamName, string teamId, IEnumerable<Player> players)
    {
        var sharePlayers = players
            .Select(player => new TeamSharePlayer
            {
                Name = player.Name,
                Position = player.Position.ToString()
            })
            .ToList();

        return new TeamShareData
        {
            Kind = TeamShareData.KindLocal,
            TeamName = teamName,
            TeamId = teamId,
            Players = sharePlayers,
            CreatedAtUtc = DateTime.UtcNow,
            DisplayTitle = teamName
        };
    }

    /// <summary>
    /// Minimal shared-team QR payload: kind indicator + invite code only (cloud holds the rest).
    /// </summary>
    public static TeamShareData CreateSharedJoinInvite(string inviteCode, string? displayTitle = null)
    {
        var code = NormalizeInviteCode(inviteCode);
        return new TeamShareData
        {
            Kind = TeamShareData.KindShared,
            InviteCode = code,
            TeamName = string.Empty,
            TeamId = string.Empty,
            Players = [],
            CreatedAtUtc = DateTime.UtcNow,
            DisplayTitle = string.IsNullOrWhiteSpace(displayTitle) ? "Shared team" : displayTitle.Trim()
        };
    }

    public static string GenerateDeepLink(TeamShareData teamData)
    {
        if (teamData.IsSharedJoin)
        {
            var code = NormalizeInviteCode(teamData.InviteCode);
            // Compact join URL — no roster payload
            return $"turf://v1/join?invite={Uri.EscapeDataString(code)}";
        }

        var payload = EncodePayload(teamData);
        return $"turf://v1/import?team={payload}";
    }

    public static string GenerateQrLink(TeamShareData teamData)
    {
        return GenerateDeepLink(teamData);
    }

    public static byte[] GenerateQrPng(string content, int size = 500, string? captionBelow = null)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new QrCodeEncodingOptions
            {
                Width = size,
                Height = size,
                Margin = 1
            }
        };

        var pixelData = writer.Write(content);
        // ZXing PixelData is BGRA32; encode with a tiny PNG writer (no ImageSharp).
        if (string.IsNullOrWhiteSpace(captionBelow))
            return PngEncoder.EncodeBgra32(pixelData.Pixels, pixelData.Width, pixelData.Height);

        // Messaging apps share the image only — bake receiver instructions under the QR.
        return QrCaptionComposer.ComposeQrWithCaption(
            pixelData.Pixels,
            pixelData.Width,
            pixelData.Height,
            captionBelow);
    }

    /// <summary>
    /// QR PNG for sharing. Shared-team invites include a hold-to-open caption under the code.
    /// </summary>
    public static byte[] GenerateShareQrPng(TeamShareData teamData, int size = 500)
    {
        var link = GenerateQrLink(teamData);
        if (teamData.IsSharedJoin)
            return GenerateQrPng(link, size, QrCaptionComposer.SharedJoinCaption);
        return GenerateQrPng(link, size);
    }

    public static int GetApproximateEncodedSize(TeamShareData teamData)
    {
        return Encoding.UTF8.GetByteCount(GenerateDeepLink(teamData));
    }

    /// <summary>Wire-format for shared joins: only kind + inviteCode (plus empty locals for schema stability).</summary>
    private static object BuildWirePayload(TeamShareData teamData)
    {
        if (teamData.IsSharedJoin)
        {
            return new
            {
                kind = TeamShareData.KindShared,
                inviteCode = NormalizeInviteCode(teamData.InviteCode)
            };
        }

        return new
        {
            kind = TeamShareData.KindLocal,
            teamName = teamData.TeamName,
            teamId = teamData.TeamId,
            players = teamData.Players,
            createdAtUtc = teamData.CreatedAtUtc
        };
    }

    private static string EncodePayload(TeamShareData teamData)
    {
        var json = JsonSerializer.Serialize(BuildWirePayload(teamData), JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static string? DecodeQrContentFromImage(Stream imageStream)
    {
        if (imageStream.CanSeek)
            imageStream.Position = 0;

        return QrImageDecoder.DecodeQrFromImageStream(imageStream);
    }

    /// <summary>
    /// True when <paramref name="raw"/> looks like a Turf Time invite/import (not OAuth /
    /// unrelated custom schemes such as <c>com.googleusercontent…</c>).
    /// Used to silently ignore non-team deep links on cold start.
    /// </summary>
    public static bool LooksLikeTurfTeamLink(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        raw = raw.Trim();

        if (raw.StartsWith("turf://", StringComparison.OrdinalIgnoreCase))
            return true;

        if (raw.StartsWith('{') || raw.StartsWith('['))
            return true;

        if (LooksLikeBareInviteCode(raw))
            return true;

        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                return false;

            var path = (uri.AbsolutePath ?? string.Empty).Trim('/').ToLowerInvariant();
            var host = (uri.Host ?? string.Empty).ToLowerInvariant();
            if (path.Contains("join", StringComparison.Ordinal) ||
                path.Contains("import", StringComparison.Ordinal) ||
                host.Equals("join", StringComparison.OrdinalIgnoreCase) ||
                host.Equals("import", StringComparison.OrdinalIgnoreCase))
                return true;

            return QueryHasInviteKey(uri.Query) ||
                   TryGetQueryValue(uri.Query, out _, "team", "import");
        }

        // Base64url team payloads are typically long and URL-safe; reject short / scheme-like strings.
        if (raw.Contains("://", StringComparison.Ordinal) || raw.Contains('.', StringComparison.Ordinal))
            return false;

        return raw.Length >= 24 && raw.All(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' or '+' or '/' or '=');
    }

    public static bool TryParseTeamShareData(string raw, out TeamShareData? teamData, out string error)
    {
        teamData = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Empty QR content.";
            return false;
        }

        raw = raw.Trim();

        // Compact shared join: turf://v1/join?invite=CODE (or code=)
        if (TryParseSharedJoinUri(raw, out var inviteFromUri, out error))
        {
            teamData = CreateSharedJoinInvite(inviteFromUri);
            return true;
        }

        if (!TryExtractPayload(raw, out var payload) || string.IsNullOrWhiteSpace(payload))
        {
            if (string.IsNullOrWhiteSpace(error))
                error = "No team payload in QR content.";
            return false;
        }

        // Payload itself might be a bare invite code (8+ alnum with optional dash)
        if (LooksLikeBareInviteCode(payload))
        {
            teamData = CreateSharedJoinInvite(payload);
            return true;
        }

        if (TryDecodeTeamSharePayload(payload, out teamData, out error))
            return true;

        // Never surface raw System.Text.Json exceptions ("'c' is an invalid start…") to users.
        if (error.Contains("invalid start of a value", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("BytePositionInLine", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("LineNumber", StringComparison.OrdinalIgnoreCase))
        {
            error = "This link is not a valid Turf Time team invite or import.";
        }

        return false;
    }

    private static bool TryParseSharedJoinUri(string raw, out string inviteCode, out string error)
    {
        inviteCode = string.Empty;
        error = string.Empty;

        if (!raw.StartsWith("turf://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return false;

        var path = (uri.AbsolutePath ?? string.Empty).Trim('/').ToLowerInvariant();
        var host = (uri.Host ?? string.Empty).ToLowerInvariant();
        // turf://v1/join  → Host=v1, AbsolutePath=/join
        var isJoin = path.Contains("join", StringComparison.Ordinal) ||
                     host.Equals("join", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith("join", StringComparison.Ordinal);

        if (!isJoin && !QueryHasInviteKey(uri.Query))
            return false;

        if (TryGetQueryValue(uri.Query, out var code, "invite", "code", "inviteCode") &&
            !string.IsNullOrWhiteSpace(code))
        {
            inviteCode = NormalizeInviteCode(code);
            if (string.IsNullOrEmpty(inviteCode))
            {
                error = "Invite code in QR is empty.";
                return false;
            }
            return true;
        }

        if (isJoin)
        {
            error = "Shared-team QR is missing an invite code.";
            return false;
        }

        return false;
    }

    private static bool QueryHasInviteKey(string query)
    {
        return TryGetQueryValue(query, out _, "invite", "code", "inviteCode");
    }

    private static bool TryGetQueryValue(string query, out string value, params string[] keys)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(query))
            return false;

        if (query.StartsWith('?'))
            query = query[1..];

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0)
                continue;

            var key = Uri.UnescapeDataString(pair[..idx]);
            if (!keys.Any(k => key.Equals(k, StringComparison.OrdinalIgnoreCase)))
                continue;

            value = idx < pair.Length - 1
                ? Uri.UnescapeDataString(pair[(idx + 1)..]).Trim()
                : string.Empty;
            return true;
        }

        return false;
    }

    private static bool LooksLikeBareInviteCode(string payload)
    {
        var code = NormalizeInviteCode(payload);
        // Typical app codes: 8 alphanumerics with optional dash mid (e.g. ABCD-EFGH)
        if (code.Length is < 6 or > 20)
            return false;
        return code.All(c => char.IsLetterOrDigit(c) || c == '-');
    }

    public static string NormalizeInviteCode(string? inviteCode)
    {
        if (string.IsNullOrWhiteSpace(inviteCode))
            return string.Empty;

        var raw = inviteCode.Trim().ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal);
        return raw;
    }

    private static bool TryExtractPayload(string raw, out string payload)
    {
        payload = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        raw = raw.Trim();
        if (raw.StartsWith("turf://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                return TryExtractPayload(uri, out payload);
            return false;
        }

        payload = raw;
        return true;
    }

    private static bool TryDecodeTeamSharePayload(string payload, out TeamShareData? teamData, out string error)
    {
        teamData = null;
        error = string.Empty;

        if (TryDecodeBase64UrlPayload(payload, out var json, out error) &&
            TryDeserializeTeamShareData(json, out teamData, out error))
        {
            return true;
        }

        if (TryDeserializeTeamShareData(payload, out teamData, out error))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(error))
        {
            error = "QR payload is not valid Turf Time team data.";
        }

        return false;
    }

    private static bool TryDecodeBase64UrlPayload(string payload, out string json, out string error)
    {
        json = string.Empty;
        error = string.Empty;

        try
        {
            var base64 = payload.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2:
                    base64 += "==";
                    break;
                case 3:
                    base64 += "=";
                    break;
            }

            var bytes = Convert.FromBase64String(base64);
            json = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (System.FormatException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryDeserializeTeamShareData(string json, out TeamShareData? teamData, out string error)
    {
        teamData = null;
        error = string.Empty;

        try
        {
            teamData = JsonSerializer.Deserialize<TeamShareData>(json, JsonOptions);

            if (teamData is null)
            {
                error = "Invalid team data.";
                return false;
            }

            // Infer kind for older local QRs (no Kind field)
            if (string.IsNullOrWhiteSpace(teamData.Kind))
            {
                teamData.Kind = !string.IsNullOrWhiteSpace(teamData.InviteCode) && teamData.Players.Count == 0
                    ? TeamShareData.KindShared
                    : TeamShareData.KindLocal;
            }

            if (teamData.IsSharedJoin)
            {
                teamData.InviteCode = NormalizeInviteCode(teamData.InviteCode);
                if (string.IsNullOrEmpty(teamData.InviteCode))
                {
                    error = "Shared-team QR is missing an invite code.";
                    return false;
                }

                teamData.DisplayTitle = "Shared team";
                return true;
            }

            // Local roster share
            teamData.Kind = TeamShareData.KindLocal;
            if (string.IsNullOrWhiteSpace(teamData.TeamName) || teamData.Players.Count == 0)
            {
                error = "Invalid team data.";
                return false;
            }

            teamData.DisplayTitle = teamData.TeamName;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryExtractPayload(Uri uri, out string payload)
    {
        payload = string.Empty;
        var query = uri.Query;
        if (string.IsNullOrWhiteSpace(query))
            return false;

        if (query.StartsWith('?'))
            query = query[1..];

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0 || idx == pair.Length - 1)
                continue;

            var key = Uri.UnescapeDataString(pair[..idx]);
            if (!key.Equals("team", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("import", StringComparison.OrdinalIgnoreCase))
                continue;

            payload = Uri.UnescapeDataString(pair[(idx + 1)..]).Trim();
            return !string.IsNullOrWhiteSpace(payload);
        }

        return false;
    }

    public static string ImportTeamToLocal(TeamShareData teamData)
    {
        if (teamData.IsSharedJoin)
            throw new InvalidOperationException("Shared-team QR must be joined via invite code, not imported as a local roster.");

        var teamId = BuildUniqueImportedTeamId(teamData.TeamId);
        var teamName = string.IsNullOrWhiteSpace(teamData.TeamName) ? "Imported Team" : teamData.TeamName.Trim();

        var players = new List<Player>();
        var snapshots = new List<PlayerSnapshot>();

        int slot = 1;
        foreach (var sharePlayer in teamData.Players)
        {
            var playerName = string.IsNullOrWhiteSpace(sharePlayer.Name)
                ? Player.DefaultName(slot)
                : sharePlayer.Name.Trim();
            var position = ParsePosition(sharePlayer.Position);

            players.Add(new Player
            {
                SlotId = slot,
                Name = playerName,
                Position = position
            });

            snapshots.Add(new PlayerSnapshot
            {
                SlotId = slot,
                Name = playerName,
                Field = position == PlayerPosition.Field,
                Bench = position == PlayerPosition.Bench,
                Goalie = position == PlayerPosition.Goalie,
                Inactive = position == PlayerPosition.Inactive,
                CounterSeconds = 0
            });

            slot++;
        }

        while (players.Count < 16)
        {
            var fillerSlot = players.Count + 1;
            players.Add(new Player { SlotId = fillerSlot, Name = Player.DefaultName(fillerSlot), Position = PlayerPosition.Bench });
            snapshots.Add(new PlayerSnapshot { SlotId = fillerSlot, Name = Player.DefaultName(fillerSlot) });
        }

        var snapshot = new RosterSnapshot
        {
            LastModifiedUtc = DateTimeOffset.UtcNow,
            MatchDurationSeconds = 90 * 60,
            HalfDurationSeconds = 45 * 60,
            MatchRemainingSeconds = 90 * 60,
            CurrentHalf = "setup",
            TimerRunning = false,
            CountdownPresetSeconds = 2 * 60,
            TeamAScore = 0,
            TeamBScore = 0,
            Players = snapshots
        };

        Preferences.Set($"{teamId}_name", teamName);
        Preferences.Set($"{teamId}_players", JsonSerializer.Serialize(players));
        Preferences.Set($"roster_snapshot_{teamId}", JsonSerializer.Serialize(snapshot));
        Preferences.Set($"setup_team_{teamId}", teamName);

        RegisterLocalTeamId(teamId);

        Preferences.Set(TeamModeKey, "local");
        Preferences.Set(TeamIdKey, teamId);
        Preferences.Set(TeamNameKey, teamName);
        Preferences.Set(UserRoleKey, "admin");

        return teamId;
    }

    /// <summary>
    /// Apply local prefs after a successful cloud join, or after <c>already_member</c> recovery
    /// (e.g. Debug reinstall wiped Preferences but Firebase Auth UID still matches <c>members/{uid}</c>).
    /// </summary>
    public static void ApplySharedJoinLocalState(
        string teamId,
        string teamName,
        string displayName,
        string? inviteCode = null,
        string role = "member",
        bool isOwner = false)
    {
        var normalizedRole = string.IsNullOrWhiteSpace(role)
            ? "member"
            : role.Trim().ToLowerInvariant();
        if (normalizedRole is not ("admin" or "member"))
            normalizedRole = "member";

        Preferences.Set(TeamModeKey, "shared");
        Preferences.Set(TeamIdKey, teamId);
        Preferences.Set(TeamNameKey, teamName);
        Preferences.Set(UserRoleKey, normalizedRole);
        Preferences.Set($"{teamId}_role", normalizedRole);
        Preferences.Set($"{teamId}_name", teamName);
        Preferences.Set($"{teamId}_isOwner", isOwner);
        Preferences.Set($"team_mode_{teamId}", "shared");
        Preferences.Set($"user_role_{teamId}", normalizedRole);
        // Persist invite so a later Promote-to-Admin can show/share it without recreating the team.
        var code = NormalizeInviteCode(inviteCode);
        if (!string.IsNullOrEmpty(code))
            Preferences.Set($"{teamId}_invite_code", code);
        UserDisplayName.Set(displayName);
        RegisterSharedTeamId(teamId);
    }

    private static void RegisterSharedTeamId(string teamId)
    {
        var teamListJson = Preferences.Get("team_id_list", "[]");
        try
        {
            var teamIds = JsonSerializer.Deserialize<List<string>>(teamListJson) ?? [];
            if (!teamIds.Contains(teamId))
            {
                teamIds.Add(teamId);
                Preferences.Set("team_id_list", JsonSerializer.Serialize(teamIds));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QrCodeService] Failed to register shared team id: {ex.Message}");
        }
    }

    private static PlayerPosition ParsePosition(string? position)
    {
        if (Enum.TryParse<PlayerPosition>(position, true, out var parsed))
            return parsed == PlayerPosition.None ? PlayerPosition.Bench : parsed;
        return PlayerPosition.Bench;
    }

    private static string BuildUniqueImportedTeamId(string? sourceTeamId)
    {
        var normalizedSource = string.IsNullOrWhiteSpace(sourceTeamId)
            ? "imported"
            : new string(sourceTeamId.Trim().Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());

        if (string.IsNullOrWhiteSpace(normalizedSource))
            normalizedSource = "imported";

        var baseId = normalizedSource.StartsWith("local_", StringComparison.Ordinal)
            ? normalizedSource
            : $"local_{normalizedSource}";

        var localIdsJson = Preferences.Get("local_team_id_list", "[]");
        List<string> localIds;
        try
        {
            localIds = JsonSerializer.Deserialize<List<string>>(localIdsJson) ?? [];
        }
        catch
        {
            localIds = [];
        }

        if (!localIds.Contains(baseId, StringComparer.Ordinal))
            return baseId;

        var unique = $"{baseId}_{Interlocked.Increment(ref _importCounter):D2}";
        while (localIds.Contains(unique, StringComparer.Ordinal))
            unique = $"{baseId}_{Interlocked.Increment(ref _importCounter):D2}";

        return unique;
    }

    private static void RegisterLocalTeamId(string teamId)
    {
        var teamListJson = Preferences.Get("local_team_id_list", "[]");
        try
        {
            var teamIds = JsonSerializer.Deserialize<List<string>>(teamListJson) ?? [];
            if (!teamIds.Contains(teamId))
            {
                teamIds.Add(teamId);
                Preferences.Set("local_team_id_list", JsonSerializer.Serialize(teamIds));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[QrCodeService] Failed to register local team id: {ex.Message}");
        }
    }
}
