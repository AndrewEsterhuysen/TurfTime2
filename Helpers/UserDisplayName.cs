namespace TurfTime2.Helpers;

/// <summary>
/// Device-local chat/team display name (Preferences) used when stamping messages
/// and member profiles. Not an OS identity — user-entered only.
/// </summary>
public static class UserDisplayName
{
	public const string PreferenceKey = "user_name";
	public const int MaxLength = 40;
	public const int MinLength = 2;

	public static string Get()
		=> (Preferences.Get(PreferenceKey, string.Empty) ?? string.Empty).Trim();

	public static void Set(string name)
		=> Preferences.Set(PreferenceKey, Normalize(name));

	public static string Normalize(string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return string.Empty;

		var trimmed = name.Trim();
		if (trimmed.Length > MaxLength)
			trimmed = trimmed[..MaxLength].Trim();
		return trimmed;
	}

	public static bool IsPlaceholder(string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return true;

		return name.Trim() switch
		{
			"Admin" or "Member" or "Someone" or "Teammate" => true,
			_ => false
		};
	}

	public static bool TryValidate(string? input, out string normalized, out string? error)
	{
		normalized = Normalize(input);
		if (normalized.Length < MinLength)
		{
			error = $"Enter a display name ({MinLength}–{MaxLength} characters) so teammates know who you are in Chat.";
			return false;
		}

		if (IsPlaceholder(normalized))
		{
			error = "Please choose a personal name (not a role like Admin or Member).";
			return false;
		}

		error = null;
		return true;
	}
}
