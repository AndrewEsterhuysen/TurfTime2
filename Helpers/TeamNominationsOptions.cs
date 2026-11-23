namespace TurfTime2;

/// <summary>
/// Per-team Player of the Match nominations switch, synced at
/// <c>teams/{teamId}/details/settings.nominationsEnabled</c>.
/// Default ON for existing shared teams (missing doc or field).
/// Admin-only writes; members may read. Local teams are unchanged
/// (nominations stay unavailable there).
/// </summary>
public static class TeamNominationsOptions
{
    public const string CloudDocName = "settings";
    public const string FieldName = "nominationsEnabled";

    private static string PrefKey(string teamId) => $"nominations_enabled_{teamId}";

    public static bool IsSharedTeam() => DetailsCloudSync.IsSharedTeam();

    public static string CurrentTeamId() => Preferences.Get("team_id", string.Empty);

    public static bool IsCurrentUserAdmin()
    {
        var mode = Preferences.Get("team_mode", string.Empty);
        if (string.Equals(mode, "local", StringComparison.OrdinalIgnoreCase))
            return true;
        var role = Preferences.Get("user_role", "admin") ?? "admin";
        return !string.Equals(role, "member", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Cached value for the current shared team. Missing cache defaults to ON.
    /// Returns false for local teams so feature gates stay off there.
    /// </summary>
    public static bool IsEnabled()
    {
        if (!IsSharedTeam()) return false;
        var teamId = CurrentTeamId();
        if (string.IsNullOrWhiteSpace(teamId)) return false;
        return Preferences.Get(PrefKey(teamId), true);
    }

    /// <summary>
    /// True when the Details → Nominations tile should show. Local teams keep
    /// the existing tile (the page then explains it is shared-team only).
    /// Shared teams hide the tile when the team switch is off.
    /// </summary>
    public static bool ShowDetailsTile() => !IsSharedTeam() || IsEnabled();

    public static void SetCached(string teamId, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(teamId)) return;
        Preferences.Set(PrefKey(teamId), enabled);
    }

    /// <summary>Pull the cloud doc into the local cache. Fail soft; keeps the cache on errors.</summary>
    public static async Task RefreshAsync()
    {
        if (!IsSharedTeam()) return;
        var teamId = CurrentTeamId();
        if (string.IsNullOrWhiteSpace(teamId)) return;

        var svc = DetailsCloudSync.Service;
        if (svc is null) return;
        try
        {
            await DetailsCloudSync.AwaitPendingSaveAsync(CloudDocName, teamId).ConfigureAwait(true);
            var doc = await svc.LoadAsync(teamId, CloudDocName).ConfigureAwait(true);
            if (doc is null || !doc.Reachable) return;
            if (!doc.Exists)
            {
                // Default ON; do not write a doc until an Admin changes the switch.
                if (!Preferences.ContainsKey(PrefKey(teamId)))
                    SetCached(teamId, true);
                return;
            }

            if (Preferences.Get($"details_{CloudDocName}_pending_upload_{teamId}", false)
                && DateTimeOffset.TryParse(Preferences.Get($"details_{CloudDocName}_last_modified_utc_{teamId}", string.Empty), out var localLm)
                && localLm > doc.LastModifiedUtc)
                return;

            SetCached(teamId, ParseEnabled(doc.Fields));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TeamNominationsOptions] Refresh: {ex.Message}");
        }
    }

    /// <summary>Admin-only. Updates the cache immediately and uploads to Firestore.</summary>
    public static async Task<bool> SetEnabledAsync(bool enabled)
    {
        if (!IsSharedTeam() || !IsCurrentUserAdmin()) return false;
        var teamId = CurrentTeamId();
        if (string.IsNullOrWhiteSpace(teamId)) return false;

        SetCached(teamId, enabled);
        DetailsCloudSync.MarkLocalEdit(CloudDocName, teamId);

        var fields = new Dictionary<string, string> { [FieldName] = enabled ? "true" : "false" };
        var svc = DetailsCloudSync.Service;
        if (svc is null) return false;
        try
        {
            var ok = await svc.SaveAsync(teamId, CloudDocName, fields).ConfigureAwait(true);
            if (ok)
                Preferences.Remove($"details_{CloudDocName}_pending_upload_{teamId}");
            return ok;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TeamNominationsOptions] Save: {ex.Message}");
            return false;
        }
    }

    internal static bool ParseEnabled(IReadOnlyDictionary<string, string> fields)
    {
        if (fields is null || !fields.TryGetValue(FieldName, out var value) || string.IsNullOrWhiteSpace(value))
            return true;
        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
    }
}
