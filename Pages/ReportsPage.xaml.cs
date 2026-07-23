using System.Text;
using System.Text.Json;
using TurfTime2.Models;

namespace TurfTime2;

public partial class ReportsPage : ContentPage
{
    private string currentHtmlReport = "";
    private string currentSavedReportPath = "";
    private List<SessionSummary> availableReports = new List<SessionSummary>();
    private Dictionary<string, string> sessionJsonCache = new Dictionary<string, string>(); // Cache session data by sessionId

    public ReportsPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        DetailsPage.ApplyPageTeamTitle(this, "Match Reports");
        await LoadReportsAsync();
    }

    private async Task LoadReportsAsync()
    {
        try
        {
            StatusLabel.Text = "Loading reports...";
            StatusLabel.IsVisible = true;

            // Get current team ID and mode
            var teamId = Preferences.Get("team_id", "");
            var teamMode = Preferences.Get("team_mode", "");

            if (string.IsNullOrEmpty(teamId))
            {
                StatusLabel.Text = "No team selected. Please select a team first.";
                currentHtmlReport = GenerateNoDataHtml();
                ReportWebView.Source = new HtmlWebViewSource { Html = currentHtmlReport };
                ViewReportButton.IsEnabled = false;
                EmailHtmlButton.IsEnabled = false;
                EmailReportButton.IsEnabled = false;
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[ReportsPage] Team ID: {teamId}, Mode: {teamMode}");

            // Load sessions based on team mode
            if (teamMode == "local")
            {
                // For local teams, load from localStorage via JavaScript
                System.Diagnostics.Debug.WriteLine($"[ReportsPage] Loading sessions from localStorage for local team");
                await LoadLocalSessionsAsync(teamId);
            }
            else
            {
                // For cloud teams, try Firestore first, fallback to localStorage
                System.Diagnostics.Debug.WriteLine($"[ReportsPage] Loading sessions from Firestore for cloud team");
                var cloudSessionsLoaded = await LoadCloudSessionsAsync(teamId);

                // Fallback: if no cloud sessions found, check localStorage (for sessions that failed to sync)
                if (!cloudSessionsLoaded || availableReports.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[ReportsPage] ⚠️ No cloud sessions found, checking localStorage fallback");
                    StatusLabel.Text = "Checking local storage for unsynced games...";
                    StatusLabel.IsVisible = true;
                    await LoadLocalSessionsAsync(teamId);

                    // Add note if we found local sessions for a cloud team
                    if (availableReports.Count > 0)
                    {
                        StatusLabel.Text = "⚠️ Showing local games (may not be synced across devices)";
                        StatusLabel.IsVisible = true;
                        await Task.Delay(3000); // Show warning for 3 seconds
                        StatusLabel.IsVisible = false;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ReportsPage] Error loading reports: {ex.Message}");
            StatusLabel.Text = $"Error loading reports: {ex.Message}";
            StatusLabel.IsVisible = true;
        }
    }

    private Task LoadLocalSessionsAsync(string teamId)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[ReportsPage] Loading local sessions from Preferences for team: {teamId}");

            const string SessionHistoryKey = "roster.sessionHistory.v1";
            var raw = Preferences.Get(SessionHistoryKey, string.Empty);

            if (string.IsNullOrEmpty(raw))
            {
                System.Diagnostics.Debug.WriteLine("[ReportsPage] No session history found in Preferences");
                ShowNoDataMessage();
                return Task.CompletedTask;
            }

            // Parse the raw JSON array directly so that event Details are never lost through
            // a typed deserialize→re-serialize round-trip (object? values don't survive that).
            using var doc = JsonDocument.Parse(raw);
            var sessionArray = doc.RootElement;

            if (sessionArray.ValueKind != JsonValueKind.Array || sessionArray.GetArrayLength() == 0)
            {
                System.Diagnostics.Debug.WriteLine("[ReportsPage] No sessions found in Preferences history");
                ShowNoDataMessage();
                return Task.CompletedTask;
            }

            availableReports.Clear();
            sessionJsonCache.Clear();

            foreach (var sessionEl in sessionArray.EnumerateArray())
            {
                // Read summary fields directly from the JSON element
                var sessionId = "";
                if (sessionEl.TryGetProperty("SessionId", out var sidProp) || sessionEl.TryGetProperty("sessionId", out sidProp))
                    sessionId = sidProp.GetString() ?? "";

                DateTime startTime = DateTime.MinValue;
                if (sessionEl.TryGetProperty("StartTime", out var stProp) || sessionEl.TryGetProperty("startTime", out stProp))
                    DateTime.TryParse(stProp.GetString(), out startTime);

                DateTime? endTime = null;
                if (sessionEl.TryGetProperty("EndTime", out var etProp) || sessionEl.TryGetProperty("endTime", out etProp))
                    if (DateTime.TryParse(etProp.GetString(), out var et)) endTime = et;

                int matchDuration = 0;
                if (sessionEl.TryGetProperty("MatchDurationSeconds", out var mdProp) || sessionEl.TryGetProperty("matchDurationSeconds", out mdProp))
                    mdProp.TryGetInt32(out matchDuration);

                if (string.IsNullOrEmpty(sessionId)) continue;

                var summary = new SessionSummary
                {
                    SessionId     = sessionId,
                    StartTime     = startTime.ToLocalTime(),
                    EndTime       = endTime?.ToLocalTime(),
                    MatchDuration = matchDuration
                };
                availableReports.Add(summary);

                // Cache the raw JSON text — no re-serialization, so no data loss
                sessionJsonCache[sessionId] = sessionEl.GetRawText();
            }

            System.Diagnostics.Debug.WriteLine($"[ReportsPage] Found {availableReports.Count} local sessions");
            PopulateGamePicker();
            StatusLabel.IsVisible = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ReportsPage] Error loading local sessions: {ex.Message}");
            ShowNoDataMessage();
        }

        return Task.CompletedTask;
    }
    private async Task<bool> LoadCloudSessionsAsync(string teamId)
    {
        try
        {
            // Load available sessions from Firestore
            availableReports = await SessionLoadHelper.LoadSessionsForTeamAsync(teamId);

            if (availableReports.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[ReportsPage] Found {availableReports.Count} cloud sessions");

                // Clear cache
                sessionJsonCache.Clear();

                // Populate picker with all available sessions
                PopulateGamePicker();

                StatusLabel.IsVisible = false;
                System.Diagnostics.Debug.WriteLine($"[ReportsPage] ✅ Cloud sessions loaded successfully");
                return true;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[ReportsPage] No cloud sessions found in Firestore");
                return false;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ReportsPage] Error loading cloud sessions: {ex.Message}");
            return false;
        }
    }

    private void PopulateGamePicker()
    {
        GameSessionPicker.Items.Clear();

        foreach (var session in availableReports)
        {
            var displayText = $"{session.StartTime:MMM dd, yyyy h:mm tt}";
            if (session.MatchDuration > 0)
            {
                var minutes = session.MatchDuration / 60;
                displayText += $" ({minutes} min)";
            }
            GameSessionPicker.Items.Add(displayText);
        }

        // Enable picker and auto-select the most recent game (first item)
        if (GameSessionPicker.Items.Count > 0)
        {
            GameSessionPicker.IsEnabled = true;
            GameSessionPicker.SelectedIndex = 0;
        }
        else
        {
            GameSessionPicker.IsEnabled = false;
        }
    }

    private async void OnGameSessionSelected(object sender, EventArgs e)
    {
        try
        {
            if (GameSessionPicker.SelectedIndex < 0 || GameSessionPicker.SelectedIndex >= availableReports.Count)
            {
                return;
            }

            StatusLabel.Text = "Loading game report...";
            StatusLabel.IsVisible = true;

            var selectedSession = availableReports[GameSessionPicker.SelectedIndex];
            System.Diagnostics.Debug.WriteLine($"[ReportsPage] Selected session: {selectedSession.SessionId}");

            string sessionJson = "";

            // Check if we have it cached (local sessions)
            if (sessionJsonCache.ContainsKey(selectedSession.SessionId))
            {
                sessionJson = sessionJsonCache[selectedSession.SessionId];
                System.Diagnostics.Debug.WriteLine($"[ReportsPage] Using cached session data");
            }
            else
            {
                // Load from Firestore (cloud sessions)
                var teamId = Preferences.Get("team_id", "");
                sessionJson = await SessionLoadHelper.LoadSessionDataAsync(teamId, selectedSession.SessionId);
                System.Diagnostics.Debug.WriteLine($"[ReportsPage] Loaded session from Firestore");
            }

            if (!string.IsNullOrEmpty(sessionJson))
            {
                currentHtmlReport = GenerateHtmlReport(sessionJson);
                currentSavedReportPath = ""; // Reset saved path for new report
                ReportWebView.Source = new HtmlWebViewSource { Html = currentHtmlReport };

                ViewReportButton.IsEnabled = true;
                EmailHtmlButton.IsEnabled = true;
                EmailReportButton.IsEnabled = true;
                StatusLabel.IsVisible = false;

                System.Diagnostics.Debug.WriteLine($"[ReportsPage] ✅ Report loaded for session: {selectedSession.SessionId}");
            }
            else
            {
                StatusLabel.Text = "Could not load game data.";
                System.Diagnostics.Debug.WriteLine($"[ReportsPage] ❌ Failed to load session data");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ReportsPage] Error loading selected game: {ex.Message}");
            StatusLabel.Text = $"Error loading game: {ex.Message}";
            StatusLabel.IsVisible = true;
        }
    }

    private void ShowNoDataMessage()
    {
        currentHtmlReport = GenerateNoDataHtml();
        ReportWebView.Source = new HtmlWebViewSource { Html = currentHtmlReport };
        GameSessionPicker.IsEnabled = false;
        ViewReportButton.IsEnabled = false;
        EmailHtmlButton.IsEnabled = false;
        EmailReportButton.IsEnabled = false;
        StatusLabel.Text = "No match data available yet. Complete a game to generate reports.";
        StatusLabel.IsVisible = true;
    }

    private string GenerateHtmlReport(string sessionJson)
    {
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html>");
        sb.AppendLine("<head>");
        sb.AppendLine("    <meta charset='utf-8'>");
        sb.AppendLine("    <meta name='viewport' content='width=device-width, initial-scale=1'>");
        sb.AppendLine("    <style>");
        sb.AppendLine("        body { font-family: Arial, sans-serif; margin: 20px; background-color: #f5f5f5; }");
        sb.AppendLine("        .header { text-align: center; font-size: 32px; font-weight: bold; color: #1b5e20; margin-bottom: 30px; }");
        sb.AppendLine("        .section { background-color: white; padding: 20px; margin-bottom: 20px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }");
        sb.AppendLine("        .section-title { font-size: 20px; font-weight: bold; color: #2e7d32; margin-bottom: 15px; border-bottom: 2px solid #2e7d32; padding-bottom: 5px; }");
        sb.AppendLine("        table { width: 100%; border-collapse: collapse; margin-top: 10px; }");
        sb.AppendLine("        th { background-color: #2e7d32; color: white; padding: 12px; text-align: left; font-weight: bold; }");
        sb.AppendLine("        td { padding: 10px; border-bottom: 1px solid #e0e0e0; }");
        sb.AppendLine("        tr:hover { background-color: #f5f5f5; }");
        sb.AppendLine("        .summary-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 15px; margin-top: 10px; }");
        sb.AppendLine("        .summary-item { padding: 15px; background-color: #e8f5e9; border-radius: 5px; }");
        sb.AppendLine("        .summary-label { font-weight: bold; color: #1b5e20; font-size: 14px; }");
        sb.AppendLine("        .summary-value { font-size: 24px; color: #2e7d32; margin-top: 5px; }");
        sb.AppendLine("        tr.score-event { background-color: #fff9c4; font-weight: bold; }");
        sb.AppendLine("        tr.score-event:hover { background-color: #fff176; }");
        sb.AppendLine("    </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        // Header with Turf Time branding
        sb.AppendLine("    <div class='header'>⚽ Turf Time 🥅</div>");

        // Parse session data and generate report sections
        try
        {
            var session = JsonDocument.Parse(sessionJson);
            var root = session.RootElement;

            // Team name (from session data or current preference)
            var teamName = "";
            if (root.TryGetProperty("TeamName", out var teamNameProp) && teamNameProp.ValueKind == JsonValueKind.String)
                teamName = teamNameProp.GetString() ?? "";
            else if (root.TryGetProperty("teamName", out var teamNameCamel) && teamNameCamel.ValueKind == JsonValueKind.String)
                teamName = teamNameCamel.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(teamName))
                teamName = Preferences.Get("team_name", "");
            if (!string.IsNullOrWhiteSpace(teamName))
                sb.AppendLine($"    <div style='text-align:center; font-size:22px; font-weight:bold; color:#1b5e20; margin-top:-20px; margin-bottom:20px;'>{teamName}</div>");

            // Match Summary Section
            sb.AppendLine("    <div class='section'>");
            sb.AppendLine("        <div class='section-title'>Match Summary</div>");
            sb.AppendLine("        <div class='summary-grid'>");

            // Final Score — top of summary, full width
            var scoreUs = 0;
            var scoreThem = 0;
            if (root.TryGetProperty("ScoreUs", out var scoreUsProp)) scoreUs = scoreUsProp.GetInt32();
            else if (root.TryGetProperty("scoreUs", out var scoreUsCamel)) scoreUs = scoreUsCamel.GetInt32();
            if (root.TryGetProperty("ScoreThem", out var scoreThemProp)) scoreThem = scoreThemProp.GetInt32();
            else if (root.TryGetProperty("scoreThem", out var scoreThemCamel)) scoreThem = scoreThemCamel.GetInt32();

            sb.AppendLine($"        <div class='summary-item' style='grid-column: 1 / -1;'>");
            sb.AppendLine($"            <div class='summary-label'>Final Score (Us – Them)</div>");
            sb.AppendLine($"            <div class='summary-value'>{scoreUs} – {scoreThem}</div>");
            sb.AppendLine($"        </div>");

            // 1. Date
            if (root.TryGetProperty("StartTime", out var startTimeProp) || root.TryGetProperty("startTime", out startTimeProp))
            {
                var startDate = DateTime.Parse(startTimeProp.GetString()!);

                sb.AppendLine($"        <div class='summary-item'>");
                sb.AppendLine($"            <div class='summary-label'>Date</div>");
                sb.AppendLine($"            <div class='summary-value'>{startDate:MMM dd, yyyy}</div>");
                sb.AppendLine($"        </div>");

                // 2. Target Duration (preset match duration)
                int targetMinutes = 0;
                if (root.TryGetProperty("MatchDurationSeconds", out var matchDurProp))
                    targetMinutes = matchDurProp.GetInt32() / 60;
                else if (root.TryGetProperty("matchDuration", out var matchDurCamel))
                    targetMinutes = matchDurCamel.GetInt32() / 60;
                sb.AppendLine($"        <div class='summary-item'>");
                sb.AppendLine($"            <div class='summary-label'>Target Duration</div>");
                sb.AppendLine($"            <div class='summary-value'>{targetMinutes} min</div>");
                sb.AppendLine($"        </div>");

                // 3. Actual Duration + 4. Start Time + 5. Full Time
                bool hasEndTime = false;
                if ((root.TryGetProperty("EndTime", out var endTimeProp) || root.TryGetProperty("endTime", out endTimeProp)) &&
                    endTimeProp.ValueKind != JsonValueKind.Null)
                {
                    var endDate = DateTime.Parse(endTimeProp.GetString()!);
                    var actualDuration = endDate - startDate;
                    var durMin = (int)actualDuration.TotalMinutes;
                    var durSec = actualDuration.Seconds;

                    sb.AppendLine($"        <div class='summary-item'>");
                    sb.AppendLine($"            <div class='summary-label'>Actual Duration</div>");
                    sb.AppendLine($"            <div class='summary-value'>{durMin}:{durSec:D2}</div>");
                    sb.AppendLine($"        </div>");

                    // 4. Start Time
                    sb.AppendLine($"        <div class='summary-item'>");
                    sb.AppendLine($"            <div class='summary-label'>Start Time</div>");
                    sb.AppendLine($"            <div class='summary-value'>{startDate:h:mm tt}</div>");
                    sb.AppendLine($"        </div>");

                    // 5. Full Time
                    sb.AppendLine($"        <div class='summary-item'>");
                    sb.AppendLine($"            <div class='summary-label'>Full Time</div>");
                    sb.AppendLine($"            <div class='summary-value'>{endDate:h:mm tt}</div>");
                    sb.AppendLine($"        </div>");

                    hasEndTime = true;
                }

                if (!hasEndTime)
                {
                    // Still show start time if no end time
                    sb.AppendLine($"        <div class='summary-item'>");
                    sb.AppendLine($"            <div class='summary-label'>Start Time</div>");
                    sb.AppendLine($"            <div class='summary-value'>{startDate:h:mm tt}</div>");
                    sb.AppendLine($"        </div>");
                }
            }

            if (root.TryGetProperty("Location", out var location) && location.ValueKind != JsonValueKind.Null && !string.IsNullOrWhiteSpace(location.GetString()))
            {
                sb.AppendLine($"        <div class='summary-item'>");
                sb.AppendLine($"            <div class='summary-label'>Location</div>");
                sb.AppendLine($"            <div class='summary-value'>{location.GetString()}</div>");
                sb.AppendLine($"        </div>");
            }

            sb.AppendLine("        </div>");
            sb.AppendLine("    </div>");

            // Match Timeline Section
            JsonElement logs;
            bool hasLogs = (root.TryGetProperty("Events", out logs) || root.TryGetProperty("logs", out logs)) && logs.GetArrayLength() > 0;
            System.Diagnostics.Debug.WriteLine($"[GenerateHtmlReport] hasLogs={hasLogs} eventCount={( hasLogs ? logs.GetArrayLength() : 0)}");
            if (hasLogs)
            {
                // Dump first 5 events for diagnostics
                int dumpCount = 0;
                foreach (var ev in logs.EnumerateArray())
                {
                    if (dumpCount++ >= 5) break;
                    var etProp = ev.TryGetProperty("EventType", out var etp) || ev.TryGetProperty("eventType", out etp) ? etp.ToString() : "?";
                    var descProp = ev.TryGetProperty("Description", out var dp) || ev.TryGetProperty("description", out dp) ? dp.GetString() : "?";
                    var hasDetails = ev.TryGetProperty("Details", out var detProp) || ev.TryGetProperty("details", out detProp);
                    System.Diagnostics.Debug.WriteLine($"[GenerateHtmlReport]   event[{dumpCount-1}] eventType={etProp} desc={descProp} hasDetails={hasDetails}");
                }
                // Score event EventType values (enum ordinals — ScoreUs=16, ScoreThem=17)
                const int ScoreUsType   = (int)GameEventType.ScoreUs;
                const int ScoreThemType = (int)GameEventType.ScoreThem;

                sb.AppendLine("    <div class='section'>");
                sb.AppendLine("        <div class='section-title'>Match Timeline</div>");
                sb.AppendLine("        <table>");
                sb.AppendLine("            <tr><th>Clock</th><th>Game Time</th><th>Event</th><th>Score</th></tr>");

                foreach (var log in logs.EnumerateArray())
                {
                    // Handle both PascalCase (C# model) and camelCase (JS/localStorage)
                    var hasTimestamp   = log.TryGetProperty("Timestamp",   out var timestamp)   || log.TryGetProperty("timestamp",   out timestamp);
                    var hasDescription = log.TryGetProperty("Description", out var description) || log.TryGetProperty("description", out description);
                    if (!hasTimestamp || !hasDescription) continue;

                    var time = DateTime.Parse(timestamp.GetString()!);
                    var desc = description.GetString() ?? "";

                    // Detect score events by EventType ordinal
                    var eventTypeVal = -1;
                    if (log.TryGetProperty("EventType", out var et) || log.TryGetProperty("eventType", out et))
                        eventTypeVal = et.ValueKind == JsonValueKind.Number ? et.GetInt32() : -1;

                    bool isScoreEvent = eventTypeVal == ScoreUsType || eventTypeVal == ScoreThemType;

                    // Read Details dictionary for game-time and score fields
                    var elapsedDisplay = "";
                    var scoreDisplay   = "";
                    var scorer         = "";
                    var assist         = "";
                    if (log.TryGetProperty("Details", out var details) || log.TryGetProperty("details", out details))
                    {
                        if (details.TryGetProperty("elapsedDisplay", out var ed))
                            elapsedDisplay = ed.GetString() ?? "";
                        if (details.TryGetProperty("scoreUs", out var su) && details.TryGetProperty("scoreThem", out var st))
                            scoreDisplay = $"{su.GetInt32()} – {st.GetInt32()}";
                        if (details.TryGetProperty("scorer", out var s))
                            scorer = s.GetString() ?? "";
                        if (details.TryGetProperty("assist", out var a))
                            assist = a.GetString() ?? "";
                    }

                    // Append scorer/assist to description if available (from Details)
                    // Note: Description already includes scorer/assist info from LogScoreEvent
                    // This code kept for backward compatibility with old events
                    if (isScoreEvent && !string.IsNullOrEmpty(scorer) && !desc.Contains(scorer))
                    {
                        desc += $"\n{scorer}";
                        if (!string.IsNullOrEmpty(assist))
                        {
                            desc += $", assisted-{assist}";
                        }
                    }

                    // Score events get a highlighted row; all others are plain
                    var rowClass = isScoreEvent ? " class='score-event'" : "";
                    sb.AppendLine($"            <tr{rowClass}>");
                    sb.AppendLine($"                <td>{time:HH:mm:ss}</td>");
                    sb.AppendLine($"                <td>{elapsedDisplay}</td>");
                    sb.AppendLine($"                <td>{desc}</td>");
                    sb.AppendLine($"                <td>{scoreDisplay}</td>");
                    sb.AppendLine($"            </tr>");
                }

                sb.AppendLine("        </table>");
                sb.AppendLine("    </div>");
            }

            // Player Statistics Section
            JsonElement summary, playerStats = default;
            bool hasSummary = (root.TryGetProperty("Summary", out summary) || root.TryGetProperty("summary", out summary)) &&
                              summary.ValueKind == JsonValueKind.Object &&
                              (summary.TryGetProperty("PlayerStats", out playerStats) || summary.TryGetProperty("playerStats", out playerStats)) &&
                              playerStats.ValueKind == JsonValueKind.Array &&
                              playerStats.GetArrayLength() > 0;
            if (hasSummary)
            {
                sb.AppendLine("    <div class='section'>");
                sb.AppendLine("        <div class='section-title'>Player Statistics</div>");
                sb.AppendLine("        <table>");
                sb.AppendLine("            <tr><th>Player</th><th>Field Time</th><th>Bench Time</th><th>Rotations In</th><th>Rotations Out</th></tr>");

                foreach (var player in playerStats.EnumerateArray())
                {
                    JsonElement name;
                    if (!player.TryGetProperty("PlayerName", out name) && !player.TryGetProperty("playerName", out name))
                        continue;

                    var fieldTime = 0;
                    if (player.TryGetProperty("FieldSeconds", out var ft) || player.TryGetProperty("timeOnField", out ft)) fieldTime = ft.GetInt32();
                    var benchTime = 0;
                    if (player.TryGetProperty("BenchSeconds", out var bt) || player.TryGetProperty("timeOnBench", out bt)) benchTime = bt.GetInt32();
                    var rotIn = 0;
                    if (player.TryGetProperty("RotationsIn", out var ri) || player.TryGetProperty("rotationsIn", out ri)) rotIn = ri.GetInt32();
                    var rotOut = 0;
                    if (player.TryGetProperty("RotationsOut", out var ro) || player.TryGetProperty("rotationsOut", out ro)) rotOut = ro.GetInt32();

                    var fieldMinutes = fieldTime / 60;
                    var fieldSeconds = fieldTime % 60;
                    var benchMinutes = benchTime / 60;
                    var benchSeconds = benchTime % 60;

                    sb.AppendLine($"            <tr>");
                    sb.AppendLine($"                <td>{name.GetString()}</td>");
                    sb.AppendLine($"                <td>{fieldMinutes}:{fieldSeconds:D2}</td>");
                    sb.AppendLine($"                <td>{benchMinutes}:{benchSeconds:D2}</td>");
                    sb.AppendLine($"                <td>{rotIn}</td>");
                    sb.AppendLine($"                <td>{rotOut}</td>");
                    sb.AppendLine($"            </tr>");
                }

                sb.AppendLine("        </table>");
                sb.AppendLine("    </div>");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ReportsPage] Error parsing session data: {ex.Message}");
            sb.AppendLine("    <div class='section'>");
            sb.AppendLine("        <p>Error generating report from session data.</p>");
            sb.AppendLine("    </div>");
        }

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    private string GenerateNoDataHtml()
    {
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html>");
        sb.AppendLine("<head>");
        sb.AppendLine("    <meta charset='utf-8'>");
        sb.AppendLine("    <style>");
        sb.AppendLine("        body { font-family: Arial, sans-serif; margin: 20px; background-color: #f5f5f5; text-align: center; }");
        sb.AppendLine("        .header { font-size: 32px; font-weight: bold; color: #1b5e20; margin: 50px 0; }");
        sb.AppendLine("        .message { font-size: 18px; color: #666; }");
        sb.AppendLine("    </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("    <div class='header'>⚽ Turf Time 🥅</div>");
        sb.AppendLine("    <div class='message'>No match data available yet.</div>");
        sb.AppendLine("    <div class='message'>Complete a game to generate reports.</div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    private async void OnEmailHtmlClicked(object sender, EventArgs e)
    {
        try
        {
            StatusLabel.Text = "Preparing HTML email...";
            StatusLabel.IsVisible = true;

            if (string.IsNullOrEmpty(currentHtmlReport))
            {
                await DisplayAlert("No Report", "No report data available to email.", "OK");
                StatusLabel.IsVisible = false;
                return;
            }

            // Use MAUI Email API
            var message = new EmailMessage
            {
                Subject = "Turf Time Match Report",
                Body = currentHtmlReport,
                BodyFormat = EmailBodyFormat.Html
            };

            await Email.ComposeAsync(message);

            StatusLabel.Text = "Email composed successfully!";
            await Task.Delay(2000);
            StatusLabel.IsVisible = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ReportsPage] Error composing email: {ex.Message}");
            await DisplayAlert("Error", $"Could not compose email: {ex.Message}", "OK");
            StatusLabel.IsVisible = false;
        }
    }

    private async void OnEmailReportClicked(object sender, EventArgs e)
    {
        try
        {
            StatusLabel.Text = "Preparing report file...";
            StatusLabel.IsVisible = true;

            if (string.IsNullOrEmpty(currentHtmlReport))
            {
                await DisplayAlert("No Report", "No report data available to email.", "OK");
                StatusLabel.IsVisible = false;
                return;
            }

            // Save HTML report to file (if not already saved)
            if (string.IsNullOrEmpty(currentSavedReportPath))
            {
                currentSavedReportPath = await SaveHtmlReportAsync(currentHtmlReport);
            }

            if (string.IsNullOrEmpty(currentSavedReportPath))
            {
                await DisplayAlert("Error", "Could not save report file.", "OK");
                StatusLabel.IsVisible = false;
                return;
            }

            // Attach HTML to email
            var message = new EmailMessage
            {
                Subject = "Turf Time Match Report",
                Body = "Please find the attached match report. Open the HTML file in any browser to view the report. You can also save it as a PDF using your browser's 'Print to PDF' feature.",
                BodyFormat = EmailBodyFormat.PlainText
            };

            message.Attachments.Add(new EmailAttachment(currentSavedReportPath));

            await Email.ComposeAsync(message);

            StatusLabel.Text = "Report file attached to email!";
            await Task.Delay(2000);
            StatusLabel.IsVisible = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ReportsPage] Error composing email with attachment: {ex.Message}");
            await DisplayAlert("Error", $"Could not compose email: {ex.Message}", "OK");
            StatusLabel.IsVisible = false;
        }
    }

    private async void OnViewReportClicked(object sender, EventArgs e)
    {
        try
        {
            StatusLabel.Text = "Opening report...";
            StatusLabel.IsVisible = true;

            if (string.IsNullOrEmpty(currentHtmlReport))
            {
                await DisplayAlert("No Report", "No report data available to view.", "OK");
                StatusLabel.IsVisible = false;
                return;
            }

            // Save HTML report to file (if not already saved)
            if (string.IsNullOrEmpty(currentSavedReportPath))
            {
                currentSavedReportPath = await SaveHtmlReportAsync(currentHtmlReport);
            }

            if (string.IsNullOrEmpty(currentSavedReportPath))
            {
                await DisplayAlert("Error", "Could not save report file.", "OK");
                StatusLabel.IsVisible = false;
                return;
            }

            // Open the HTML file in the default browser
            await Launcher.OpenAsync(new OpenFileRequest
            {
                File = new ReadOnlyFile(currentSavedReportPath)
            });

            StatusLabel.Text = "Report opened!";
            await Task.Delay(2000);
            StatusLabel.IsVisible = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ReportsPage] Error opening report: {ex.Message}");
            await DisplayAlert("Error", $"Could not open report: {ex.Message}", "OK");
            StatusLabel.IsVisible = false;
        }
    }

    private async Task<string> SaveHtmlReportAsync(string html)
    {
        try
        {
            // Create Reports directory in AppDataDirectory (persistent storage)
            var reportsDir = Path.Combine(FileSystem.AppDataDirectory, "Reports");
            Directory.CreateDirectory(reportsDir);

            // Get current selected session for filename
            string fileName;
            if (GameSessionPicker.SelectedIndex >= 0 && GameSessionPicker.SelectedIndex < availableReports.Count)
            {
                var session = availableReports[GameSessionPicker.SelectedIndex];
                // Use session ID and date for unique filename
                fileName = $"TurfTime_Report_{session.StartTime:yyyyMMdd_HHmmss}_{session.SessionId.Substring(0, 8)}.html";
            }
            else
            {
                // Fallback to timestamp only
                fileName = $"TurfTime_Report_{DateTime.Now:yyyyMMdd_HHmmss}.html";
            }

            var filePath = Path.Combine(reportsDir, fileName);

            // Save HTML to file
            await File.WriteAllTextAsync(filePath, html);

            System.Diagnostics.Debug.WriteLine($"[ReportsPage] ✅ HTML report saved: {filePath}");
            return filePath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ReportsPage] Error saving HTML report: {ex.Message}");
            StatusLabel.Text = $"Report save failed: {ex.Message}";
            return null;
        }
    }
}
