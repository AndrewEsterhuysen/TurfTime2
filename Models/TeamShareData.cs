namespace TurfTime2.Models;

public sealed class TeamShareData
{
    public string TeamName { get; set; } = string.Empty;
    public string TeamId { get; set; } = string.Empty;
    public List<TeamSharePlayer> Players { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class TeamSharePlayer
{
    public string Name { get; set; } = string.Empty;
    public string Position { get; set; } = string.Empty;
}
