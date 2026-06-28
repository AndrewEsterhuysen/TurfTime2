using System.Text;
using System.Text.Json;
using TurfTime2.Models;

namespace TurfTime2.Services;

public static class QrCodeService
{
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
        var json = JsonSerializer.Serialize(teamData);
        var bytes = Encoding.UTF8.GetBytes(json);
        var payload = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return $"turf://v1/import?team={payload}";
    }

    public static int GetApproximateEncodedSize(TeamShareData teamData)
    {
        return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(teamData));
    }
}
