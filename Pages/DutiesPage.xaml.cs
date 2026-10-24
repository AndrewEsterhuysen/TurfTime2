namespace TurfTime2;

public partial class DutiesPage : ContentPage
{
	private const string CloudDocName = "duties";

	private bool _isLoadingData;
	private bool _isAdmin = true;
	// Blocks saves until the first load has populated the UI.
	private bool _hasLoaded;
	// Admin edited something this visit (cloud teams upload on leave only when set).
	private bool _isDirty;

	// Cloud field name → local Preferences base key (team id suffix added by GetTeamKey).
	private static readonly (string Field, string PrefBase)[] FieldMap =
	{
		("officer", "duties_officer"),
		("canteen", "duties_canteen"),
		("groundsSetup", "duties_grounds_setup"),
		("groundsPackup", "duties_grounds_packup"),
		("other", "duties_other"),
	};

	private static string GetTeamId() => Preferences.Get("team_id", string.Empty);

	private string GetTeamKey(string baseKey)
	{
		var teamId = GetTeamId();
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

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		DetailsPage.ApplyPageTeamTitle(this, "Duties");
		ApplyEditPermissions();
		LoadDutiesData();

		if (DetailsCloudSync.IsSharedTeam())
		{
			var teamId = GetTeamId();
			_hasLoaded = false;
			// Read-only while the cloud copy loads so edits can't be overwritten by it.
			var wasAdmin = _isAdmin;
			_isAdmin = false;
			ApplyReadOnlyWhileLoading();
			try
			{
				// Let a just-finished leave-save land before reading the cloud copy.
				await DetailsCloudSync.AwaitPendingSaveAsync(CloudDocName, teamId);
				var map = FieldMap.ToDictionary(f => f.Field, f => GetTeamKey(f.PrefBase));
				if (await DetailsCloudSync.PullAsync(CloudDocName, teamId, map))
					LoadDutiesData();
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[Duties] Cloud load: {ex.Message}");
			}
			_isAdmin = wasAdmin;
		}

		_isDirty = false;
		_hasLoaded = true;
		ApplyEditPermissions();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		if (!_isAdmin || !_hasLoaded)
			return;

		SaveDutiesData();

		if (DetailsCloudSync.IsSharedTeam())
		{
			var teamId = GetTeamId();
			if (_isDirty || DetailsCloudSync.HasPendingUpload(CloudDocName, teamId))
			{
				DetailsCloudSync.StartLeaveSave(CloudDocName, teamId, BuildCloudFields());
				_isDirty = false;
			}
		}
	}

	private Dictionary<string, string> BuildCloudFields() => new()
	{
		["officer"] = DutyOfficerEntry.Text ?? string.Empty,
		["canteen"] = CanteenEntry.Text ?? string.Empty,
		["groundsSetup"] = GroundsSetupEntry.Text ?? string.Empty,
		["groundsPackup"] = GroundsPackupEntry.Text ?? string.Empty,
		["other"] = OtherEntry.Text ?? string.Empty,
	};

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

	private void ApplyReadOnlyWhileLoading()
	{
		foreach (var entry in this.GetVisualTreeDescendants().OfType<Entry>())
			SetEntryEditState(entry, false);
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
		if (_hasLoaded && DetailsCloudSync.IsSharedTeam())
		{
			_isDirty = true;
			DetailsCloudSync.MarkLocalEdit(CloudDocName, GetTeamId());
		}
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
