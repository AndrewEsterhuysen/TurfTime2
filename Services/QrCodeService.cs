using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
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
            TeamName = teamName,
            TeamId = teamId,
            Players = sharePlayers,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    public static string GenerateDeepLink(TeamShareData teamData)
    {
        var payload = EncodePayload(teamData);
        return $"turf://v1/import?team={payload}";
    }

    public static string GenerateQrLink(TeamShareData teamData)
    {
        return GenerateDeepLink(teamData);
    }

    public static byte[] GenerateQrPng(string content, int size = 500)
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
        using var image = SixLabors.ImageSharp.Image.LoadPixelData<Bgra32>(pixelData.Pixels, pixelData.Width, pixelData.Height);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    public static int GetApproximateEncodedSize(TeamShareData teamData)
    {
        return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(teamData));
    }

    private static string EncodePayload(TeamShareData teamData)
    {
        var json = JsonSerializer.Serialize(teamData);
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static string? DecodeQrContentFromImage(Stream imageStream)
    {
        imageStream.Position = 0;
        using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(imageStream);
        var pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixels);

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = [BarcodeFormat.QR_CODE]
            }
        };

        return reader.Decode(pixels, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.RGBA32)?.Text;
    }

    public static bool TryParseTeamShareData(string raw, out TeamShareData? teamData, out string error)
    {
        teamData = null;
        error = string.Empty;

        if (!TryExtractPayload(raw, out var payload) || string.IsNullOrWhiteSpace(payload))
        {
            error = "No team payload in QR content.";
            return false;
        }

        if (TryDecodeTeamSharePayload(payload, out teamData, out error))
        {
            return true;
        }

        return false;
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
            teamData = JsonSerializer.Deserialize<TeamShareData>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (teamData is null || string.IsNullOrWhiteSpace(teamData.TeamName) || teamData.Players.Count == 0)
            {
                error = "Invalid team data.";
                return false;
            }

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
        var teamId = BuildUniqueImportedTeamId(teamData.TeamId);
        var teamName = string.IsNullOrWhiteSpace(teamData.TeamName) ? "Imported Team" : teamData.TeamName.Trim();

        var players = new List<Player>();
        var snapshots = new List<PlayerSnapshot>();

        int slot = 1;
        foreach (var sharePlayer in teamData.Players)
        {
            var playerName = string.IsNullOrWhiteSpace(sharePlayer.Name) ? $"Player {slot}" : sharePlayer.Name.Trim();
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
            players.Add(new Player { SlotId = fillerSlot, Name = $"Player {fillerSlot}", Position = PlayerPosition.None });
            snapshots.Add(new PlayerSnapshot { SlotId = fillerSlot, Name = $"Player {fillerSlot}" });
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

    private static PlayerPosition ParsePosition(string? position)
    {
        if (Enum.TryParse<PlayerPosition>(position, true, out var parsed))
            return parsed;
        return PlayerPosition.None;
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
