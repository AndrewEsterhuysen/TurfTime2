namespace TurfTime2;

public partial class SetupPage : ContentPage
{
	private const string LOCATION_PICK_KEY = "location_pick_coordinates";
	private const string SETUP_DATA_KEY = "setup_data.v1";
	private bool _isLoadingData = false;

	public SetupPage()
	{
		InitializeComponent();
		LoadMatchData();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		// Check if returning from maps with coordinates
		CheckForPickedLocation();
		// Reload data in case it was updated elsewhere
		LoadMatchData();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		// Save data when leaving the page
		SaveMatchData();
	}

	private void OnFieldChanged(object sender, EventArgs e)
	{
		// Don't save while loading data to avoid feedback loop
		if (!_isLoadingData)
		{
			SaveMatchData();
		}
	}

	private void LoadMatchData()
	{
		try
		{
			_isLoadingData = true;

			// Load from Preferences
			var teamName = Preferences.Get("setup_team", string.Empty);
			var matchDate = Preferences.Get("setup_match_date", DateTime.Today.ToString("O"));
			var matchTime = Preferences.Get("setup_match_time", DateTime.Now.ToString("HH:mm:ss"));
			var duration = Preferences.Get("setup_duration", string.Empty);
			var locationName = Preferences.Get("setup_location_name", string.Empty);
			var latitude = Preferences.Get("setup_latitude", string.Empty);
			var longitude = Preferences.Get("setup_longitude", string.Empty);
			var mapsLink = Preferences.Get("setup_maps_link", string.Empty);

			// Populate fields
			if (!string.IsNullOrEmpty(teamName))
				TeamEntry.Text = teamName;

			if (DateTime.TryParse(matchDate, out var parsedDate))
				MatchDatePicker.Date = parsedDate;

			if (TimeSpan.TryParse(matchTime, out var parsedTime))
				MatchTimePicker.Time = parsedTime;

			if (!string.IsNullOrEmpty(duration))
				DurationEntry.Text = duration;

			if (!string.IsNullOrEmpty(locationName))
				LocationNameEntry.Text = locationName;

			if (!string.IsNullOrEmpty(latitude))
				LatitudeEntry.Text = latitude;

			if (!string.IsNullOrEmpty(longitude))
				LongitudeEntry.Text = longitude;

			if (!string.IsNullOrEmpty(mapsLink))
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
		try
		{
			// Save all fields to Preferences
			Preferences.Set("setup_team", TeamEntry.Text ?? string.Empty);
			Preferences.Set("setup_match_date", value: MatchDatePicker.Date.ToString());
			Preferences.Set("setup_match_time", MatchTimePicker.Time.ToString());
			Preferences.Set("setup_duration", DurationEntry.Text ?? string.Empty);
			Preferences.Set("setup_location_name", LocationNameEntry.Text ?? string.Empty);
			Preferences.Set("setup_latitude", LatitudeEntry.Text ?? string.Empty);
			Preferences.Set("setup_longitude", LongitudeEntry.Text ?? string.Empty);
			Preferences.Set("setup_maps_link", MapsLinkEntry.Text ?? string.Empty);

			System.Diagnostics.Debug.WriteLine("Match data saved successfully");
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"Error saving match data: {ex.Message}");
		}
	}

	private void CheckForPickedLocation()
	{
		// Check if we have coordinates from map picker
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
				}
				Preferences.Remove(LOCATION_PICK_KEY);
			}
		}
	}

	private async void OnGetLocationClicked(object sender, EventArgs e)
	{
		try
		{
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
			}
			else
			{
				await DisplayAlert("Location Error", "Unable to get current location. Please enter coordinates manually.", "OK");
			}
		}
		catch (FeatureNotSupportedException)
		{
			await DisplayAlert("Not Supported", "Geolocation is not supported on this device.", "OK");
		}
		catch (FeatureNotEnabledException)
		{
			await DisplayAlert("Location Disabled", "Please enable location services in device settings.", "OK");
		}
		catch (PermissionException)
		{
			await DisplayAlert("Permission Denied", "Location permission is required to get GPS coordinates.", "OK");
		}
		catch (Exception ex)
		{
			await DisplayAlert("Error", $"Unable to get location: {ex.Message}", "OK");
		}
	}

	private async void OnSearchLocationClicked(object sender, EventArgs e)
	{
		try
		{
			var locationName = LocationNameEntry.Text?.Trim();
			
			if (string.IsNullOrWhiteSpace(locationName))
			{
				await DisplayAlert("No Location", "Please enter a location name first.", "OK");
				return;
			}

			// Build Google Maps search URL
			var searchQuery = Uri.EscapeDataString(locationName);
			var mapsUrl = $"https://www.google.com/maps/search/?api=1&query={searchQuery}";

			// Open in Google Maps
			if (Uri.TryCreate(mapsUrl, UriKind.Absolute, out var uri))
			{
				await Launcher.OpenAsync(uri);
			}
			else
			{
				await DisplayAlert("Error", "Unable to create search URL.", "OK");
			}
		}
		catch (Exception ex)
		{
			await DisplayAlert("Error", $"Unable to search location: {ex.Message}", "OK");
		}
	}

	private async void OnPickLocationClicked(object sender, EventArgs e)
	{
		try
		{
			// Get current location or use default if available
			double lat = 0;
			double lon = 0;
			
			if (!string.IsNullOrWhiteSpace(LatitudeEntry.Text) && !string.IsNullOrWhiteSpace(LongitudeEntry.Text))
			{
				double.TryParse(LatitudeEntry.Text, out lat);
				double.TryParse(LongitudeEntry.Text, out lon);
			}
			else
			{
				// Try to get current location as starting point
				var location = await Geolocation.GetLastKnownLocationAsync();
				if (location != null)
				{
					lat = location.Latitude;
					lon = location.Longitude;
				}
			}

			var locationName = LocationNameEntry.Text ?? "Match Location";

			// Create a placemark for the map
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

			// Open map to show/pick location
			await Map.OpenAsync(placemark, options);

			// Show instructions to user
			await DisplayAlert("Pick Location", 
				"1. Find your desired location on the map\n" +
				"2. Long-press on the location to drop a pin\n" +
				"3. Tap the pin and copy the coordinates\n" +
				"4. Return to this app and paste the coordinates", 
				"OK");
		}
		catch (Exception ex)
		{
			await DisplayAlert("Error", $"Unable to open maps: {ex.Message}", "OK");
		}
	}

	private async void OnOpenLinkClicked(object sender, EventArgs e)
	{
		try
		{
			var link = MapsLinkEntry.Text?.Trim();
			
			if (string.IsNullOrWhiteSpace(link))
			{
				await DisplayAlert("No Link", "Please paste a Google Maps link first.", "OK");
				return;
			}

			// Validate and extract coordinates from Google Maps link if possible
			if (TryExtractCoordinatesFromLink(link, out double lat, out double lon))
			{
				// Update coordinate fields
				LatitudeEntry.Text = lat.ToString("F6");
				LongitudeEntry.Text = lon.ToString("F6");
			}

			// Open the link in browser/maps app
			if (Uri.TryCreate(link, UriKind.Absolute, out var uri))
			{
				await Launcher.OpenAsync(uri);
			}
			else
			{
				await DisplayAlert("Invalid Link", "The provided link is not valid. Please check and try again.", "OK");
			}
		}
		catch (Exception ex)
		{
			await DisplayAlert("Error", $"Unable to open link: {ex.Message}", "OK");
		}
	}

	private bool TryExtractCoordinatesFromLink(string link, out double latitude, out double longitude)
	{
		latitude = 0;
		longitude = 0;

		try
		{
			// Google Maps link formats:
			// 1. https://maps.google.com/?q=lat,lng
			// 2. https://www.google.com/maps/@lat,lng,zoom
			// 3. https://www.google.com/maps/place/.../@lat,lng
			// 4. https://goo.gl/maps/... (shortened, can't extract directly)
			// 5. https://maps.app.goo.gl/... (new shortened format)

			var uri = new Uri(link);
			
			// Try to extract from query parameter
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

			// Try to extract from path (format: /@lat,lng,zoom)
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

			// Try alternative format with /place/
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