using Microsoft.Extensions.DependencyInjection;
using TurfTime2.Helpers;
using TurfTime2.Models;
using TurfTime2.Services;

namespace TurfTime2;

public partial class SetupPage : ContentPage
{
	private const string LOCATION_PICK_KEY = "location_pick_coordinates";
	private bool _isLoadingData;
	private bool _isAdmin = true;
	private IMatchScheduleService? _scheduleService;
	private MatchSchedule? _currentSchedule;

	private string GetTeamId() => Preferences.Get("team_id", string.Empty);

	private static bool IsSharedTeam()
	{
		var mode = Preferences.Get("team_mode", string.Empty);
		var teamId = Preferences.Get("team_id", string.Empty);
		if (string.Equals(mode, "local", StringComparison.OrdinalIgnoreCase)) return false;
		if (teamId.StartsWith("local_", StringComparison.Ordinal)) return false;
		return !string.IsNullOrWhiteSpace(teamId) && string.Equals(mode, "shared", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsCurrentUserAdmin()
	{
		var role = Preferences.Get("user_role", "admin") ?? "admin";
		var mode = Preferences.Get("team_mode", string.Empty);
		if (string.Equals(mode, "local", StringComparison.OrdinalIgnoreCase))
			return true;
		return !string.Equals(role, "member", StringComparison.OrdinalIgnoreCase);
	}

	public SetupPage()
	{
		InitializeComponent();
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		DetailsPage.ApplyPageTeamTitle(this, "Location");
		ApplyEditPermissions();
		CheckForPickedLocation();

		ResolveServices();
		MatchScheduleSyncHost.ScheduleChanged += OnScheduleChangedFromSync;

		// App-level host keeps shared teams in sync even when this page is closed.
		var host = GetService<MatchScheduleSyncHost>();
		if (host is not null)
			_ = host.EnsureForCurrentTeamAsync();

		await LoadMatchDataAsync();
		ApplyEditPermissions();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		MatchScheduleSyncHost.ScheduleChanged -= OnScheduleChangedFromSync;
		if (_isAdmin)
			_ = SaveMatchDataAsync(forceCloud: true);
	}

	private void ResolveServices()
	{
		_scheduleService ??= GetService<IMatchScheduleService>();
	}

	private static T? GetService<T>() where T : class
	{
		var services = Application.Current?.Handler?.MauiContext?.Services
			?? Shell.Current?.Handler?.MauiContext?.Services;
		return services?.GetService<T>();
	}

	private void OnScheduleChangedFromSync(object? sender, MatchSchedule schedule)
	{
		var teamId = GetTeamId();
		if (string.IsNullOrEmpty(teamId) || !string.Equals(schedule.TeamId, teamId, StringComparison.Ordinal))
			return;
		if (_isLoadingData) return;

		// Don't clobber in-progress admin edits with a stale echo of their own save.
		if (_isAdmin && _currentSchedule is not null
		    && schedule.LastModifiedUtc != default
		    && _currentSchedule.LastModifiedUtc != default
		    && schedule.LastModifiedUtc < _currentSchedule.LastModifiedUtc)
			return;

		ApplyScheduleToUi(schedule);
	}

	private void ApplyEditPermissions()
	{
		_isAdmin = IsCurrentUserAdmin();
		ViewOnlyBanner.IsVisible = !_isAdmin;

		MatchDatePicker.IsEnabled = _isAdmin;
		MatchTimePicker.IsEnabled = _isAdmin;
		ArriveTimePicker.IsEnabled = _isAdmin;
		LocationNameEntry.IsEnabled = _isAdmin;
		LocationNameEntry.IsReadOnly = !_isAdmin;
		LatitudeEntry.IsEnabled = _isAdmin;
		LatitudeEntry.IsReadOnly = !_isAdmin;
		LongitudeEntry.IsEnabled = _isAdmin;
		LongitudeEntry.IsReadOnly = !_isAdmin;
		MapsLinkEntry.IsEnabled = _isAdmin;
		MapsLinkEntry.IsReadOnly = !_isAdmin;

		GetLocationButton.IsVisible = _isAdmin;
		PickLocationButton.IsVisible = _isAdmin;

		SearchLocationButton.IsEnabled = true;
		OpenLinkButton.IsEnabled = true;
	}

	private void OnFieldChanged(object sender, EventArgs e)
	{
		if (_isLoadingData || !_isAdmin)
			return;
		_ = SaveMatchDataAsync(forceCloud: false);
	}

	private async Task LoadMatchDataAsync()
	{
		try
		{
			_isLoadingData = true;
			ResolveServices();
			var teamId = GetTeamId();
			if (string.IsNullOrWhiteSpace(teamId) || _scheduleService is null)
			{
				ApplyScheduleToUi(null);
				return;
			}

			var preferCloud = IsSharedTeam() && !IsCurrentUserAdmin();
			// Admins also pull cloud once so co-admin edits show up.
			if (IsSharedTeam())
				preferCloud = true;

			var schedule = await _scheduleService.LoadAsync(teamId, preferCloud).ConfigureAwait(true);
			schedule ??= _scheduleService.LoadLocal(teamId);
			ApplyScheduleToUi(schedule);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"Error loading match data: {ex.Message}");
		}
		finally
		{
			_isLoadingData = false;
		}
	}

	private void ApplyScheduleToUi(MatchSchedule? schedule)
	{
		try
		{
			_isLoadingData = true;
			_currentSchedule = schedule;

			if (schedule is not null && DateTime.TryParse(schedule.MatchDate, out var parsedDate))
				MatchDatePicker.Date = parsedDate;
			else
				MatchDatePicker.Date = DateTime.Today;

			if (schedule is not null && TimeSpan.TryParse(schedule.MatchTime, out var parsedTime))
				MatchTimePicker.Time = parsedTime;

			if (schedule is not null && TimeSpan.TryParse(schedule.ArriveTime, out var parsedArrive))
				ArriveTimePicker.Time = parsedArrive;
			else if (schedule is not null && TimeSpan.TryParse(schedule.MatchTime, out var fallbackArrive))
				ArriveTimePicker.Time = fallbackArrive;

			LocationNameEntry.Text = schedule?.LocationName ?? string.Empty;
			LatitudeEntry.Text = schedule?.Latitude ?? string.Empty;
			LongitudeEntry.Text = schedule?.Longitude ?? string.Empty;
			MapsLinkEntry.Text = schedule?.MapsLink ?? string.Empty;

			UpdateStatusLabels(schedule);
		}
		finally
		{
			_isLoadingData = false;
		}
	}

	private void UpdateStatusLabels(MatchSchedule? schedule)
	{
		var status = MatchScheduleEvaluator.Evaluate(schedule);
		var statusText = MatchScheduleEvaluator.StatusLabel(status);
		ScheduleStatusLabel.Text = string.IsNullOrEmpty(statusText) ? string.Empty : $"Status: {statusText}";

		ScheduleStatusLabel.TextColor = status switch
		{
			MatchScheduleStatus.Upcoming => Color.FromArgb("#2E7D32"),
			MatchScheduleStatus.Past => Color.FromArgb("#E65100"),
			MatchScheduleStatus.Incomplete => Color.FromArgb("#F9A825"),
			_ => Color.FromArgb("#757575")
		};

		var updated = MatchScheduleEvaluator.FormatLastUpdated(schedule);
		ScheduleUpdatedLabel.Text = updated;
		ScheduleUpdatedLabel.IsVisible = !string.IsNullOrEmpty(updated);

		ScheduleSourceLabel.Text = MatchScheduleEvaluator.FormatSourceLine(schedule, IsSharedTeam());

		var showBanner = status is MatchScheduleStatus.Past or MatchScheduleStatus.Incomplete;
		ScheduleStatusBanner.IsVisible = showBanner;
		ScheduleStatusBannerLabel.Text = status switch
		{
			MatchScheduleStatus.Past => "⚠ Past match — this schedule is outdated. Set the next fixture.",
			MatchScheduleStatus.Incomplete => "⚠ Incomplete — set match date and kickoff time.",
			_ => string.Empty
		};
		ScheduleStatusBanner.BackgroundColor = status == MatchScheduleStatus.Past
			? Color.FromArgb("#FFF3E0")
			: Color.FromArgb("#FFFDE7");
	}

	private MatchSchedule BuildScheduleFromUi()
	{
		var teamId = GetTeamId();
		var uid = Preferences.Get("user_id", string.Empty);
		var displayName = UserDisplayName.Get();

		return new MatchSchedule
		{
			SchemaVersion = MatchSchedule.CurrentSchemaVersion,
			TeamId = teamId,
			MatchDate = $"{MatchDatePicker.Date:yyyy-MM-dd}",
			MatchTime = MatchTimePicker.Time.ToString(),
			ArriveTime = ArriveTimePicker.Time.ToString(),
			LocationName = LocationNameEntry.Text ?? string.Empty,
			Latitude = LatitudeEntry.Text ?? string.Empty,
			Longitude = LongitudeEntry.Text ?? string.Empty,
			MapsLink = MapsLinkEntry.Text ?? string.Empty,
			LastModifiedUtc = DateTimeOffset.UtcNow,
			UpdatedByUid = string.IsNullOrWhiteSpace(uid) ? null : uid,
			UpdatedByDisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
			FromCloud = false
		};
	}

	private async Task SaveMatchDataAsync(bool forceCloud)
	{
		if (!_isAdmin)
			return;

		try
		{
			ResolveServices();
			var teamId = GetTeamId();
			if (string.IsNullOrWhiteSpace(teamId))
				return;

			var schedule = BuildScheduleFromUi();
			_currentSchedule = schedule;
			UpdateStatusLabels(schedule);

			if (_scheduleService is null)
			{
				// Extremely defensive: persist keys the old way if DI not ready.
				Preferences.Set($"setup_match_date_{teamId}", schedule.MatchDate);
				Preferences.Set($"setup_match_time_{teamId}", schedule.MatchTime);
				Preferences.Set($"setup_arrive_time_{teamId}", schedule.ArriveTime);
				Preferences.Set($"setup_location_name_{teamId}", schedule.LocationName);
				Preferences.Set($"setup_latitude_{teamId}", schedule.Latitude);
				Preferences.Set($"setup_longitude_{teamId}", schedule.Longitude);
				Preferences.Set($"setup_maps_link_{teamId}", schedule.MapsLink);
				return;
			}

			if (forceCloud)
				await _scheduleService.ForceSyncAsync(teamId, schedule);
			else
				await _scheduleService.SaveAsync(teamId, schedule, isAdmin: true);

			System.Diagnostics.Debug.WriteLine("[SetupPage] Match schedule saved");
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"Error saving match data: {ex.Message}");
		}
	}

	private void CheckForPickedLocation()
	{
		if (!_isAdmin)
			return;

		if (Preferences.ContainsKey(LOCATION_PICK_KEY))
		{
			var coords = Preferences.Get(LOCATION_PICK_KEY, string.Empty);
			if (!string.IsNullOrEmpty(coords))
			{
				var parts = coords.Split(',');
				if (parts.Length == 2)
				{
					LatitudeEntry.Text = parts[0].Trim();
					LongitudeEntry.Text = parts[1].Trim();
					_ = SaveMatchDataAsync(forceCloud: false);
				}
				Preferences.Remove(LOCATION_PICK_KEY);
			}
		}
	}

	private async void OnGetLocationClicked(object sender, EventArgs e)
	{
		if (!_isAdmin)
			return;

		try
		{
#if WINDOWS
			await DisplayAlertAsync("Not Available on Windows",
				"GPS location is not available on Windows desktop. Please enter coordinates manually or use the 'Search Location' or 'Paste Maps Link' features.",
				"OK");
			return;
#else
			var location = await Geolocation.GetLastKnownLocationAsync();

			if (location == null)
			{
				location = await Geolocation.GetLocationAsync(new GeolocationRequest
				{
					DesiredAccuracy = GeolocationAccuracy.Medium,
					Timeout = TimeSpan.FromSeconds(10)
				});
			}

			if (location != null)
			{
				LatitudeEntry.Text = location.Latitude.ToString("F6");
				LongitudeEntry.Text = location.Longitude.ToString("F6");
				await SaveMatchDataAsync(forceCloud: false);
			}
			else
			{
				await DisplayAlertAsync("Location Error", "Unable to get current location. Please enter coordinates manually.", "OK");
			}
#endif
		}
		catch (FeatureNotSupportedException)
		{
			await DisplayAlertAsync("Not Supported", "Geolocation is not supported on this device.", "OK");
		}
		catch (FeatureNotEnabledException)
		{
			await DisplayAlertAsync("Location Disabled", "Please enable location services in device settings.", "OK");
		}
		catch (PermissionException)
		{
			await DisplayAlertAsync("Permission Denied", "Location permission is required to get GPS coordinates.", "OK");
		}
		catch (Exception ex)
		{
			await DisplayAlertAsync("Error", $"Unable to get location: {ex.Message}", "OK");
		}
	}

	private async void OnSearchLocationClicked(object sender, EventArgs e)
	{
		try
		{
			var locationName = LocationNameEntry.Text?.Trim();

			if (string.IsNullOrWhiteSpace(locationName))
			{
				await DisplayAlertAsync("No Location", "Please enter a location name first.", "OK");
				return;
			}

			var searchQuery = Uri.EscapeDataString(locationName);
			var mapsUrl = $"https://www.google.com/maps/search/?api=1&query={searchQuery}";

			if (Uri.TryCreate(mapsUrl, UriKind.Absolute, out var uri))
			{
				await Launcher.OpenAsync(uri);
			}
			else
			{
				await DisplayAlertAsync("Error", "Unable to create search URL.", "OK");
			}
		}
		catch (Exception ex)
		{
			await DisplayAlertAsync("Error", $"Unable to search location: {ex.Message}", "OK");
		}
	}

	private async void OnPickLocationClicked(object sender, EventArgs e)
	{
		if (!_isAdmin)
			return;

		try
		{
#if WINDOWS
			await DisplayAlertAsync("Not Available on Windows",
				"Map picker is not available on Windows desktop. Please use 'Search Location' to open Google Maps in your browser, or paste a Google Maps link.",
				"OK");
			return;
#else
			await DisplayAlertAsync(
				"Pick Location",
				"1. Find your desired location on the map\n" +
				"2. Long-press on the location to drop a pin\n" +
				"3. Tap the pin and copy the coordinates\n" +
				"4. Return to this app and paste the coordinates into Latitude / Longitude",
				"Open Maps");

			double lat = 0;
			double lon = 0;

			if (!string.IsNullOrWhiteSpace(LatitudeEntry.Text) && !string.IsNullOrWhiteSpace(LongitudeEntry.Text))
			{
				double.TryParse(LatitudeEntry.Text, out lat);
				double.TryParse(LongitudeEntry.Text, out lon);
			}
			else
			{
				var location = await Geolocation.GetLastKnownLocationAsync();
				if (location != null)
				{
					lat = location.Latitude;
					lon = location.Longitude;
				}
			}

			var locationName = LocationNameEntry.Text ?? "Match Location";

			var placemark = new Placemark
			{
				Location = new Location(lat, lon),
				Thoroughfare = locationName
			};

			var options = new MapLaunchOptions
			{
				Name = locationName,
				NavigationMode = NavigationMode.None
			};

			await Map.OpenAsync(placemark, options);
#endif
		}
		catch (Exception ex)
		{
			await DisplayAlertAsync("Error", $"Unable to open maps: {ex.Message}", "OK");
		}
	}

	private async void OnOpenLinkClicked(object sender, EventArgs e)
	{
		try
		{
			var link = MapsLinkEntry.Text?.Trim();

			if (string.IsNullOrWhiteSpace(link))
			{
				await DisplayAlertAsync("No Link", "Please paste a Google Maps link first.", "OK");
				return;
			}

			if (TryExtractCoordinatesFromLink(link, out double lat, out double lon))
			{
				if (_isAdmin)
				{
					LatitudeEntry.Text = lat.ToString("F6");
					LongitudeEntry.Text = lon.ToString("F6");
					await SaveMatchDataAsync(forceCloud: false);
				}
			}

			if (Uri.TryCreate(link, UriKind.Absolute, out var uri))
			{
				await Launcher.OpenAsync(uri);
			}
			else
			{
				await DisplayAlertAsync("Invalid Link", "The provided link is not valid. Please check and try again.", "OK");
			}
		}
		catch (Exception ex)
		{
			await DisplayAlertAsync("Error", $"Unable to open link: {ex.Message}", "OK");
		}
	}

	private bool TryExtractCoordinatesFromLink(string link, out double latitude, out double longitude)
	{
		latitude = 0;
		longitude = 0;

		try
		{
			var uri = new Uri(link);

			if (link.Contains("?q="))
			{
				var query = uri.Query.TrimStart('?');
				var parts = query.Replace("q=", "").Split(',');
				if (parts.Length >= 2 &&
					double.TryParse(parts[0], out latitude) &&
					double.TryParse(parts[1], out double lng))
				{
					longitude = lng;
					return true;
				}
			}

			var path = uri.AbsolutePath;
			if (path.Contains("/@"))
			{
				var coordsPart = path.Substring(path.IndexOf("/@") + 2);
				var parts = coordsPart.Split(',');
				if (parts.Length >= 2 &&
					double.TryParse(parts[0], out latitude) &&
					double.TryParse(parts[1], out double lng))
				{
					longitude = lng;
					return true;
				}
			}

			if (path.Contains("/place/"))
			{
				var coordsPart = path.Substring(path.LastIndexOf("/@") + 2);
				var parts = coordsPart.Split(',');
				if (parts.Length >= 2 &&
					double.TryParse(parts[0], out latitude) &&
					double.TryParse(parts[1], out double lng))
				{
					longitude = lng;
					return true;
				}
			}
		}
		catch
		{
			// If parsing fails, just return false
		}

		return false;
	}
}
