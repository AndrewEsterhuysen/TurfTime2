namespace TurfTime2;

public partial class KitPage : ContentPage
{
	private const string CloudDocName = "kit";

	private bool _isLoadingData;
	private bool _isAdmin = true;
	// Blocks saves until the first load has populated the UI.
	private bool _hasLoaded;
	// Admin edited something this visit (cloud teams upload on leave only when set).
	private bool _isDirty;

	// Cloud field name → local Preferences base key (team id suffix added by GetTeamKey).
	private static readonly (string Field, string PrefBase)[] FieldMap =
	{
		("arrive", "kit_arrive"),
		("warmUp", "kit_warmup"),
		("game", "kit_game"),
		("departure", "kit_departure"),
		("nonPlaying", "kit_non_playing"),
		("specialEvent", "kit_special_event"),
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

	public KitPage()
	{
		InitializeComponent();
		LoadKitData();
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		DetailsPage.ApplyPageTeamTitle(this, "Kit");
		ApplyEditPermissions();
		LoadKitData();

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
					LoadKitData();
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[Kit] Cloud load: {ex.Message}");
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

		SaveKitData();

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
		["arrive"] = ArriveEntry.Text ?? string.Empty,
		["warmUp"] = WarmUpEntry.Text ?? string.Empty,
		["game"] = GameEntry.Text ?? string.Empty,
		["departure"] = DepartureEntry.Text ?? string.Empty,
		["nonPlaying"] = NonPlayingEntry.Text ?? string.Empty,
		["specialEvent"] = SpecialEventEntry.Text ?? string.Empty,
	};

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
		SaveKitData();
		if (_hasLoaded && DetailsCloudSync.IsSharedTeam())
		{
			_isDirty = true;
			DetailsCloudSync.MarkLocalEdit(CloudDocName, GetTeamId());
		}
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
