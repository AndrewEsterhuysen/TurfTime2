namespace TurfTime2;

public partial class SetupPage : ContentPage
{
	private const string LOCATION_PICK_KEY = "location_pick_coordinates";
	private bool _isLoadingData;
	private bool _isAdmin = true;

	// Helper to get team-specific key (uses same team_id as rest of app)
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

	public SetupPage()
	{
		InitializeComponent();
		LoadMatchData();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		DetailsPage.ApplyPageTeamTitle(this, "Location");
		ApplyEditPermissions();
		// Check if returning from maps with coordinates (admin only will apply)
		CheckForPickedLocation();
		// Reload data in case it was updated elsewhere
		LoadMatchData();
		ApplyEditPermissions();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		if (_isAdmin)
			SaveMatchData();
	}

	private void ApplyEditPermissions()
	{
		_isAdmin = IsCurrentUserAdmin();
		ViewOnlyBanner.IsVisible = !_isAdmin;

		// Viewers can see all data; only admin can change it
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

		// Admin-only location capture controls
		GetLocationButton.IsVisible = _isAdmin;
		PickLocationButton.IsVisible = _isAdmin;

		// Search / Open still useful for members navigating to the ground
		SearchLocationButton.IsEnabled = true;
		OpenLinkButton.IsEnabled = true;
	}

	private void OnFieldChanged(object sender, EventArgs e)
	{
		if (_isLoadingData || !_isAdmin)
			return;
		SaveMatchData();
	}

	private void LoadMatchData()
	{
		try
		{
			_isLoadingData = true;

			var matchDate = Preferences.Get(GetTeamKey("setup_match_date"), DateTime.Today.ToString("O"));
			var matchTime = Preferences.Get(GetTeamKey("setup_match_time"), DateTime.Now.ToString("HH:mm:ss"));
			var arriveTime = Preferences.Get(GetTeamKey("setup_arrive_time"), string.Empty);
			var locationName = Preferences.Get(GetTeamKey("setup_location_name"), string.Empty);
			var latitude = Preferences.Get(GetTeamKey("setup_latitude"), string.Empty);
			var longitude = Preferences.Get(GetTeamKey("setup_longitude"), string.Empty);
			var mapsLink = Preferences.Get(GetTeamKey("setup_maps_link"), string.Empty);

			if (DateTime.TryParse(matchDate, out var parsedDate))
				MatchDatePicker.Date = parsedDate;

			if (TimeSpan.TryParse(matchTime, out var parsedTime))
				MatchTimePicker.Time = parsedTime;

			if (TimeSpan.TryParse(arriveTime, out var parsedArrive))
				ArriveTimePicker.Time = parsedArrive;
			else if (TimeSpan.TryParse(matchTime, out var fallbackArrive))
				// Default arrive to match start if never set
				ArriveTimePicker.Time = fallbackArrive;

			LocationNameEntry.Text = locationName;
			LatitudeEntry.Text = latitude;
			LongitudeEntry.Text = longitude;
			MapsLinkEntry.Text = mapsLink;
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

	private void SaveMatchData()
	{
		if (!_isAdmin)
			return;

		try
		{
			Preferences.Set(GetTeamKey("setup_match_date"), $"{MatchDatePicker.Date:yyyy-MM-dd}");
			Preferences.Set(GetTeamKey("setup_match_time"), MatchTimePicker.Time.ToString());
			Preferences.Set(GetTeamKey("setup_arrive_time"), ArriveTimePicker.Time.ToString());
			Preferences.Set(GetTeamKey("setup_location_name"), LocationNameEntry.Text ?? string.Empty);
			Preferences.Set(GetTeamKey("setup_latitude"), LatitudeEntry.Text ?? string.Empty);
			Preferences.Set(GetTeamKey("setup_longitude"), LongitudeEntry.Text ?? string.Empty);
			Preferences.Set(GetTeamKey("setup_maps_link"), MapsLinkEntry.Text ?? string.Empty);

			System.Diagnostics.Debug.WriteLine("Match data saved successfully");
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
					SaveMatchData();
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
				SaveMatchData();
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
			// Instructions first — open Maps only after the user acknowledges
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

			// Extract coordinates when possible (admin gets fields updated; members still open the link)
			if (TryExtractCoordinatesFromLink(link, out double lat, out double lon))
			{
				if (_isAdmin)
				{
					LatitudeEntry.Text = lat.ToString("F6");
					LongitudeEntry.Text = lon.ToString("F6");
					SaveMatchData();
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
