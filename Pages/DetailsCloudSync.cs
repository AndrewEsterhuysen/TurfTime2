using TurfTime2.Services;

namespace TurfTime2;

/// <summary>
/// Shared cloud plumbing for Details → Kit / Duties on shared (cloud) teams.
/// Local Preferences remain the on-device cache; local teams never touch the cloud.
/// </summary>
internal static class DetailsCloudSync
{
	private static readonly Dictionary<string, Task> PendingLeaveSaves = new();
	private static readonly object Gate = new();

	internal static bool IsSharedTeam()
	{
		var mode = Preferences.Get("team_mode", string.Empty);
		var teamId = Preferences.Get("team_id", string.Empty);
		if (string.Equals(mode, "local", StringComparison.OrdinalIgnoreCase)) return false;
		if (teamId.StartsWith("local_", StringComparison.Ordinal)) return false;
		return !string.IsNullOrWhiteSpace(teamId) && string.Equals(mode, "shared", StringComparison.OrdinalIgnoreCase);
	}

	internal static ITeamDetailsDocService? Service =>
		(Application.Current?.Handler?.MauiContext?.Services
		 ?? Shell.Current?.Handler?.MauiContext?.Services)
		?.GetService(typeof(ITeamDetailsDocService)) as ITeamDetailsDocService;

	private static string LastModKey(string docName, string teamId) => $"details_{docName}_last_modified_utc_{teamId}";
	private static string PendingUploadKey(string docName, string teamId) => $"details_{docName}_pending_upload_{teamId}";

	/// <summary>Wait (bounded) for a leave-save of this doc so a stale cloud read can't win.</summary>
	internal static async Task AwaitPendingSaveAsync(string docName, string teamId)
	{
		Task? pending;
		lock (Gate) PendingLeaveSaves.TryGetValue($"{docName}|{teamId}", out pending);
		if (pending is null) return;
		try { await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(true); }
		catch { /* best-effort */ }
	}

	/// <summary>
	/// Load the cloud doc and mirror it into local Preferences (key → pref key map).
	/// Returns true when local prefs were updated from cloud. If the cloud doc doesn't exist
	/// yet but this device has local values, they are flagged for upload on the next Admin save
	/// (one-time migration of device-only data).
	/// </summary>
	internal static async Task<bool> PullAsync(string docName, string teamId,
		IReadOnlyDictionary<string, string> fieldToPrefKey)
	{
		var svc = Service;
		if (svc is null) return false;
		try
		{
			var doc = await svc.LoadAsync(teamId, docName).ConfigureAwait(true);
			if (doc is null || !doc.Reachable) return false;

			if (!doc.Exists)
			{
				var hasLocal = fieldToPrefKey.Values.Any(k => !string.IsNullOrEmpty(Preferences.Get(k, string.Empty)));
				if (hasLocal)
					Preferences.Set(PendingUploadKey(docName, teamId), true);
				return false;
			}

			// Unsynced local edits newer than the cloud copy win (e.g. failed upload while offline).
			if (Preferences.Get(PendingUploadKey(docName, teamId), false)
			    && DateTimeOffset.TryParse(Preferences.Get(LastModKey(docName, teamId), string.Empty), out var localLm)
			    && localLm > doc.LastModifiedUtc)
				return false;

			foreach (var (field, prefKey) in fieldToPrefKey)
				Preferences.Set(prefKey, doc.Fields.TryGetValue(field, out var v) ? v : string.Empty);
			if (doc.LastModifiedUtc != default)
				Preferences.Set(LastModKey(docName, teamId), doc.LastModifiedUtc.ToString("o"));
			Preferences.Remove(PendingUploadKey(docName, teamId));
			return true;
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[DetailsCloudSync] Pull {docName}: {ex.Message}");
			return false;
		}
	}

	/// <summary>True when local data still needs uploading (migration or earlier failure).</summary>
	internal static bool HasPendingUpload(string docName, string teamId)
		=> Preferences.Get(PendingUploadKey(docName, teamId), false);

	/// <summary>Mark local edit time; call after writing Preferences on a user edit.</summary>
	internal static void MarkLocalEdit(string docName, string teamId)
	{
		Preferences.Set(LastModKey(docName, teamId), DateTimeOffset.UtcNow.ToString("o"));
		Preferences.Set(PendingUploadKey(docName, teamId), true);
	}

	/// <summary>Start a background upload on leave; tracked so the next visit can await it.</summary>
	internal static void StartLeaveSave(string docName, string teamId, IReadOnlyDictionary<string, string> fields)
	{
		var task = UploadAsync(docName, teamId, fields);
		lock (Gate) PendingLeaveSaves[$"{docName}|{teamId}"] = task;
	}

	private static async Task UploadAsync(string docName, string teamId, IReadOnlyDictionary<string, string> fields)
	{
		try
		{
			var svc = Service;
			if (svc is null) return;
			var ok = await svc.SaveAsync(teamId, docName, fields).ConfigureAwait(false);
			if (ok)
				Preferences.Remove(PendingUploadKey(docName, teamId));
			System.Diagnostics.Debug.WriteLine($"[DetailsCloudSync] Leave save {docName}: {(ok ? "OK" : "failed, will retry next save")}");
		}
		catch (Exception ex)
		{
			// Never break navigation; pending flag stays set so the next Admin visit retries.
			System.Diagnostics.Debug.WriteLine($"[DetailsCloudSync] Leave save {docName}: {ex.Message}");
		}
	}
}
