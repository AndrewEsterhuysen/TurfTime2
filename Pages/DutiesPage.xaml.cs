namespace TurfTime2;

public partial class DutiesPage : ContentPage
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

	public DutiesPage()
	{
		InitializeComponent();
		LoadDutiesData();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		DetailsPage.ApplyPageTeamTitle(this, "Duties");
		ApplyEditPermissions();
		LoadDutiesData();
		ApplyEditPermissions();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		if (_isAdmin)
			SaveDutiesData();
	}

	private void ApplyEditPermissions()
	{
		_isAdmin = IsCurrentUserAdmin();
		ViewOnlyBanner.IsVisible = !_isAdmin;

		SetEntryEditState(DutyOfficerEntry, _isAdmin);
		SetEntryEditState(CanteenEntry, _isAdmin);
		SetEntryEditState(GroundsSetupEntry, _isAdmin);
		SetEntryEditState(GroundsPackupEntry, _isAdmin);
		SetEntryEditState(OtherEntry, _isAdmin);
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
		SaveDutiesData();
	}

	private void LoadDutiesData()
	{
		try
		{
			_isLoadingData = true;

			DutyOfficerEntry.Text = Preferences.Get(GetTeamKey("duties_officer"), string.Empty);
			CanteenEntry.Text = Preferences.Get(GetTeamKey("duties_canteen"), string.Empty);
			GroundsSetupEntry.Text = Preferences.Get(GetTeamKey("duties_grounds_setup"), string.Empty);
			GroundsPackupEntry.Text = Preferences.Get(GetTeamKey("duties_grounds_packup"), string.Empty);
			OtherEntry.Text = Preferences.Get(GetTeamKey("duties_other"), string.Empty);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"Error loading duties data: {ex.Message}");
		}
		finally
		{
			_isLoadingData = false;
		}
	}

	private void SaveDutiesData()
	{
		if (!_isAdmin)
			return;

		try
		{
			Preferences.Set(GetTeamKey("duties_officer"), DutyOfficerEntry.Text ?? string.Empty);
			Preferences.Set(GetTeamKey("duties_canteen"), CanteenEntry.Text ?? string.Empty);
			Preferences.Set(GetTeamKey("duties_grounds_setup"), GroundsSetupEntry.Text ?? string.Empty);
			Preferences.Set(GetTeamKey("duties_grounds_packup"), GroundsPackupEntry.Text ?? string.Empty);
			Preferences.Set(GetTeamKey("duties_other"), OtherEntry.Text ?? string.Empty);

			System.Diagnostics.Debug.WriteLine("Duties data saved successfully");
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"Error saving duties data: {ex.Message}");
		}
	}
}
