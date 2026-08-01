namespace TurfTime2;

public partial class KitPage : ContentPage
{
	private bool _isLoadingData;
	private bool _isAdmin = true;

	private string GetTeamKey(string baseKey)
	{
		var teamId = Preferences.Get("team_id", string.Empty);
		return string.IsNullOrEmpty(teamId) ? baseKey : $"{baseKey}_{teamId}";
	}

	private static bool IsCurrentUserAdmin()
	{
		var role = Preferences.Get("user_role", "admin") ?? "admin";
		// Local teams are always editable; shared members are view-only
		var mode = Preferences.Get("team_mode", string.Empty);
		if (string.Equals(mode, "local", StringComparison.OrdinalIgnoreCase))
			return true;
		return !string.Equals(role, "member", StringComparison.OrdinalIgnoreCase);
	}

	public KitPage()
	{
		InitializeComponent();
		LoadKitData();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		DetailsPage.ApplyPageTeamTitle(this, "Kit");
		ApplyEditPermissions();
		LoadKitData();
		ApplyEditPermissions();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		if (_isAdmin)
			SaveKitData();
	}

	private void ApplyEditPermissions()
	{
		_isAdmin = IsCurrentUserAdmin();
		ViewOnlyBanner.IsVisible = !_isAdmin;

		SetEntryEditState(ArriveEntry, _isAdmin);
		SetEntryEditState(WarmUpEntry, _isAdmin);
		SetEntryEditState(GameEntry, _isAdmin);
		SetEntryEditState(DepartureEntry, _isAdmin);
		SetEntryEditState(NonPlayingEntry, _isAdmin);
		SetEntryEditState(SpecialEventEntry, _isAdmin);
	}

	private static void SetEntryEditState(Entry entry, bool canEdit)
	{
		entry.IsEnabled = canEdit;
		entry.IsReadOnly = !canEdit;
	}

	private void OnFieldChanged(object sender, EventArgs e)
	{
		if (_isLoadingData || !_isAdmin)
			return;
		SaveKitData();
	}

	private void LoadKitData()
	{
		try
		{
			_isLoadingData = true;

			ArriveEntry.Text = Preferences.Get(GetTeamKey("kit_arrive"), string.Empty);
			WarmUpEntry.Text = Preferences.Get(GetTeamKey("kit_warmup"), string.Empty);
			GameEntry.Text = Preferences.Get(GetTeamKey("kit_game"), string.Empty);
			DepartureEntry.Text = Preferences.Get(GetTeamKey("kit_departure"), string.Empty);
			NonPlayingEntry.Text = Preferences.Get(GetTeamKey("kit_non_playing"), string.Empty);
			SpecialEventEntry.Text = Preferences.Get(GetTeamKey("kit_special_event"), string.Empty);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"Error loading kit data: {ex.Message}");
		}
		finally
		{
			_isLoadingData = false;
		}
	}

	private void SaveKitData()
	{
		if (!_isAdmin)
			return;

		try
		{
			Preferences.Set(GetTeamKey("kit_arrive"), ArriveEntry.Text ?? string.Empty);
			Preferences.Set(GetTeamKey("kit_warmup"), WarmUpEntry.Text ?? string.Empty);
			Preferences.Set(GetTeamKey("kit_game"), GameEntry.Text ?? string.Empty);
			Preferences.Set(GetTeamKey("kit_departure"), DepartureEntry.Text ?? string.Empty);
			Preferences.Set(GetTeamKey("kit_non_playing"), NonPlayingEntry.Text ?? string.Empty);
			Preferences.Set(GetTeamKey("kit_special_event"), SpecialEventEntry.Text ?? string.Empty);

			System.Diagnostics.Debug.WriteLine("Kit data saved successfully");
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"Error saving kit data: {ex.Message}");
		}
	}
}
