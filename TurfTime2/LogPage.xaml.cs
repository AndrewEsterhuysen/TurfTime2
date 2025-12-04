using Microsoft.Maui.Storage;
using System.Text;

namespace TurfTime2;

public partial class LogPage : ContentPage
{
    public LogPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        
        // Initialize with empty HTML first
        logWebView.Source = new HtmlWebViewSource { Html = GetInitialHtml() };
        
        // Wait for WebView to load, then load sessions
        await Task.Delay(500);
        await LoadSessionsAsync();
    }

    private string GetInitialHtml()
    {
        return @"
<!DOCTYPE html>
<html>
<head>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <style>
        body { 
            font-family: Arial, sans-serif; 
            background: linear-gradient(135deg, #2e7d32, #1b5e20); 
            color: #fff; 
            display: flex;
            align-items: center;
            justify-content: center;
            height: 100vh;
            margin: 0;
            padding: 20px;
            text-align: center;
        }
        .message {
            font-size: 1.2em;
            opacity: 0.8;
        }
    </style>
</head>
<body>
    <div class='message'>Loading sessions...</div>
</body>
</html>";
    }

    private async Task LoadSessionsAsync()
    {
        try
        {
            // Return the data as a BASE64-encoded string to avoid escaping issues
            var script = @"
                (function() {
                    try {
                        var currentRaw = localStorage.getItem('roster.currentSession.v1');
                        var historyRaw = localStorage.getItem('roster.sessionHistory.v1');
                        
                        var result = {
                            current: currentRaw ? JSON.parse(currentRaw) : null,
                            history: historyRaw ? JSON.parse(historyRaw) : { sessions: [] }
                        };
                        
                        // Convert to base64 to avoid escaping issues
                        var jsonStr = JSON.stringify(result);
                        return btoa(unescape(encodeURIComponent(jsonStr)));
                    } catch (e) {
                        var errorResult = JSON.stringify({ error: e.message, current: null, history: { sessions: [] } });
                        return btoa(unescape(encodeURIComponent(errorResult)));
                    }
                })();
            ";

            var result = await logWebView.EvaluateJavaScriptAsync(script);
            
            System.Diagnostics.Debug.WriteLine($"[LogPage] Base64 result length: {result?.Length ?? 0}");
            
            if (string.IsNullOrEmpty(result) || result == "null")
            {
                await DisplayNoSessionsMessage();
                return;
            }

            // Decode from base64
            string jsonString;
            try
            {
                // The result might still be JSON-encoded, so try to decode it as a string first
                string base64String = result;
                if (result.StartsWith("\""))
                {
                    base64String = System.Text.Json.JsonSerializer.Deserialize<string>(result);
                }
                
                var bytes = Convert.FromBase64String(base64String);
                jsonString = System.Text.Encoding.UTF8.GetString(bytes);
                System.Diagnostics.Debug.WriteLine($"[LogPage] Decoded JSON: {jsonString.Substring(0, Math.Min(200, jsonString.Length))}...");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogPage] Failed to decode base64: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[LogPage] Raw result: {result}");
                await DisplayNoSessionsMessage();
                return;
            }

            // Parse the JSON data
            var data = System.Text.Json.JsonDocument.Parse(jsonString);
            
            // Check for errors
            if (data.RootElement.TryGetProperty("error", out var errorProp))
            {
                var error = errorProp.GetString();
                System.Diagnostics.Debug.WriteLine($"[LogPage] JavaScript error: {error}");
                await DisplayNoSessionsMessage();
                return;
            }

            var sessionList = new List<SessionInfo>();

            // Add current session if exists
            if (data.RootElement.TryGetProperty("current", out var currentProp) && 
                currentProp.ValueKind != System.Text.Json.JsonValueKind.Null &&
                currentProp.TryGetProperty("session", out var sessionProp))
            {
                var sessionId = sessionProp.GetProperty("sessionId").GetString();
                var startTime = sessionProp.GetProperty("startTime").GetString();
                System.Diagnostics.Debug.WriteLine($"[LogPage] Found current session: {sessionId}");
                sessionList.Add(new SessionInfo
                {
                    Id = sessionId,
                    DisplayName = $"?? Current Game - {FormatDateTime(startTime)}",
                    IsActive = true
                });
            }

            // Add historical sessions
            if (data.RootElement.TryGetProperty("history", out var historyProp) &&
                historyProp.TryGetProperty("sessions", out var sessionsProp))
            {
                var count = 0;
                foreach (var session in sessionsProp.EnumerateArray())
                {
                    var sessionId = session.GetProperty("sessionId").GetString();
                    var startTime = session.GetProperty("startTime").GetString();
                    var location = session.TryGetProperty("location", out var locProp) && locProp.ValueKind != System.Text.Json.JsonValueKind.Null
                        ? locProp.GetString()
                        : null;

                    var displayName = string.IsNullOrEmpty(location)
                        ? $"?? {FormatDateTime(startTime)}"
                        : $"?? {FormatDateTime(startTime)} - {location}";

                    sessionList.Add(new SessionInfo
                    {
                        Id = sessionId,
                        DisplayName = displayName,
                        IsActive = false
                    });
                    count++;
                }
                System.Diagnostics.Debug.WriteLine($"[LogPage] Found {count} historical sessions");
            }

            if (sessionList.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[LogPage] Total sessions found: {sessionList.Count}");
                sessionPicker.ItemsSource = sessionList;
                sessionPicker.ItemDisplayBinding = new Binding("DisplayName");
                sessionPicker.SelectedIndex = 0;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[LogPage] No sessions found");
                await DisplayNoSessionsMessage();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LogPage] Error loading sessions: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[LogPage] Stack trace: {ex.StackTrace}");
            await DisplayAlert("Error", $"Failed to load game sessions: {ex.Message}", "OK");
            await DisplayNoSessionsMessage();
        }
    }

    private async void OnSessionSelected(object sender, EventArgs e)
    {
        if (sessionPicker.SelectedItem is SessionInfo session)
        {
            await LoadSessionLogsAsync(session.Id, session.IsActive);
        }
    }

    private async Task LoadSessionLogsAsync(string sessionId, bool isActive)
    {
        try
        {
            // Get specific session from localStorage with base64 encoding
            var script = $@"
                (function() {{
                    try {{
                        var sessionId = '{sessionId}';
                        var session = null;
                        
                        // Check current session first
                        var currentRaw = localStorage.getItem('roster.currentSession.v1');
                        if (currentRaw) {{
                            var current = JSON.parse(currentRaw);
                            if (current.session && current.session.sessionId === sessionId) {{
                                session = current.session;
                            }}
                        }}
                        
                        // Check history if not found
                        if (!session) {{
                            var historyRaw = localStorage.getItem('roster.sessionHistory.v1');
                            if (historyRaw) {{
                                var history = JSON.parse(historyRaw);
                                if (history.sessions) {{
                                    session = history.sessions.find(s => s.sessionId === sessionId);
                                }}
                            }}
                        }}
                        
                        if (session) {{
                            var jsonStr = JSON.stringify(session);
                            return btoa(unescape(encodeURIComponent(jsonStr)));
                        }}
                        return null;
                    }} catch (e) {{
                        var errorResult = JSON.stringify({{ error: e.message }});
                        return btoa(unescape(encodeURIComponent(errorResult)));
                    }}
                }})();
            ";

            var result = await logWebView.EvaluateJavaScriptAsync(script);

            if (string.IsNullOrEmpty(result) || result == "null")
            {
                await DisplayAlert("Error", "Session not found", "OK");
                return;
            }

            // Decode from base64
            string jsonString;
            try
            {
                string base64String = result;
                if (result.StartsWith("\""))
                {
                    base64String = System.Text.Json.JsonSerializer.Deserialize<string>(result);
                }
                
                var bytes = Convert.FromBase64String(base64String);
                jsonString = System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogPage] Failed to decode session base64: {ex.Message}");
                await DisplayAlert("Error", "Failed to load session", "OK");
                return;
            }

            var session = System.Text.Json.JsonDocument.Parse(jsonString);
            
            // Check for errors
            if (session.RootElement.TryGetProperty("error", out var errorProp))
            {
                await DisplayAlert("Error", $"Failed to load session: {errorProp.GetString()}", "OK");
                return;
            }

            var html = GenerateLogHtml(session.RootElement);
            logWebView.Source = new HtmlWebViewSource { Html = html };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LogPage] Error loading session logs: {ex.Message}");
            await DisplayAlert("Error", $"Failed to load session logs: {ex.Message}", "OK");
        }
    }

    private string GenerateLogHtml(System.Text.Json.JsonElement session)
    {
        var sb = new StringBuilder();
        
        sb.Append(@"
<!DOCTYPE html>
<html>
<head>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <style>
        body { 
            font-family: Arial, sans-serif; 
            background: linear-gradient(135deg, #2e7d32, #1b5e20); 
            color: #fff; 
            padding: 10px;
            margin: 0;
        }
        .log-header {
            background: rgba(255, 255, 255, 0.1);
            padding: 15px;
            border-radius: 8px;
            margin-bottom: 15px;
        }
        .log-header h2 {
            color: #FF6B35;
            margin: 0 0 10px 0;
        }
        .log-header p {
            margin: 5px 0;
            font-size: 0.9em;
        }
        .log-entry {
            background: rgba(255, 255, 255, 0.08);
            border-left: 4px solid #FF6B35;
            padding: 10px;
            margin: 10px 0;
            border-radius: 4px;
        }
        .log-entry.type-player { border-left-color: #00e676; }
        .log-entry.type-timer { border-left-color: #ffeb3b; }
        .log-entry.type-game { border-left-color: #FF6B35; }
        .log-entry.type-rotation { border-left-color: #8B4513; }
        .timestamp { 
            color: #bbb; 
            font-size: 0.85em; 
            display: block;
            margin-bottom: 5px;
        }
        .event-type { 
            display: inline-block;
            padding: 3px 8px;
            border-radius: 4px;
            font-size: 0.75em;
            font-weight: bold;
            margin: 5px 5px 5px 0;
        }
        .type-player-badge { background: #00e676; color: #222; }
        .type-timer-badge { background: #ffeb3b; color: #222; }
        .type-game-badge { background: #FF6B35; color: #fff; }
        .type-rotation-badge { background: #8B4513; color: #fff; }
        .description { 
            font-size: 1em;
            margin: 5px 0;
        }
        .details { 
            margin-top: 8px; 
            padding: 8px;
            background: rgba(0, 0, 0, 0.2);
            border-radius: 4px;
            font-size: 0.85em;
        }
        .details-row {
            margin: 3px 0;
        }
        .no-logs {
            text-align: center;
            padding: 40px 20px;
            font-size: 1.1em;
            opacity: 0.7;
        }
    </style>
</head>
<body>");
        // Header
        var sessionId = session.GetProperty("sessionId").GetString();
        var startTime = session.GetProperty("startTime").GetString();
        var endTime = session.TryGetProperty("endTime", out var endProp) && endProp.ValueKind != System.Text.Json.JsonValueKind.Null
            ? endProp.GetString()
            : null;
        var location = session.TryGetProperty("location", out var locProp) && locProp.ValueKind != System.Text.Json.JsonValueKind.Null
            ? locProp.GetString()
            : null;

        sb.Append("<div class='log-header'>");
        sb.Append($"<h2>??? Game Session</h2>");
        sb.Append($"<p><strong>Started:</strong> {FormatDateTime(startTime)}</p>");
        if (!string.IsNullOrEmpty(endTime))
        {
            sb.Append($"<p><strong>Ended:</strong> {FormatDateTime(endTime)}</p>");
        }
        if (!string.IsNullOrEmpty(location))
        {
            sb.Append($"<p><strong>Location:</strong> {location}</p>");
        }

        if (session.TryGetProperty("summary", out var summary) && summary.ValueKind != System.Text.Json.JsonValueKind.Null)
        {
            var rotations = summary.GetProperty("totalRotations").GetInt32();
            var duration = summary.GetProperty("duration").GetInt32();
            sb.Append($"<p><strong>Rotations:</strong> {rotations} | <strong>Duration:</strong> {FormatDuration(duration)}</p>");
        }
        sb.Append("</div>");

        // Logs - Display in reverse order (latest first)
        if (session.TryGetProperty("logs", out var logs) && logs.GetArrayLength() > 0)
        {
            // Convert to list and reverse
            var logList = new List<System.Text.Json.JsonElement>();
            foreach (var log in logs.EnumerateArray())
            {
                logList.Add(log);
            }
            logList.Reverse();
            
            foreach (var log in logList)
            {
                var eventType = log.GetProperty("eventType").GetString();
                var category = GetEventCategory(eventType);
                
                sb.Append($"<div class='log-entry type-{category}'>");
                
                var timestamp = log.GetProperty("timestamp").GetString();
                sb.Append($"<span class='timestamp'>{FormatTimestamp(timestamp)}</span>");
                
                sb.Append($"<span class='event-type type-{category}-badge'>{FormatEventType(eventType)}</span>");
                
                var description = log.GetProperty("description").GetString();
                sb.Append($"<div class='description'>{description}</div>");
                
                // Details
                if (log.TryGetProperty("details", out var details) && details.ValueKind != System.Text.Json.JsonValueKind.Null)
                {
                    if (details.TryGetProperty("gameState", out var gameState) && gameState.ValueKind != System.Text.Json.JsonValueKind.Null)
                    {
                        sb.Append("<div class='details'>");
                        
                        if (gameState.TryGetProperty("currentHalf", out var half))
                            sb.Append($"<div class='details-row'><strong>Half:</strong> {half.GetString()}</div>");
                        
                        if (gameState.TryGetProperty("matchTimeRemaining", out var matchTime))
                            sb.Append($"<div class='details-row'><strong>Match Time:</strong> {FormatDuration(matchTime.GetInt32())}</div>");
                        
                        if (gameState.TryGetProperty("rotationTimeRemaining", out var rotTime))
                            sb.Append($"<div class='details-row'><strong>Rotation Time:</strong> {FormatDuration(rotTime.GetInt32())}</div>");
                        
                        sb.Append("</div>");
                    }
                }
                
                sb.Append("</div>");
            }
        }
        else
        {
            sb.Append("<div class='no-logs'>No events logged in this session</div>");
        }

        sb.Append("</body></html>");
        
        return sb.ToString();
    }

    private async Task DisplayNoSessionsMessage()
    {
        var html = @"
<!DOCTYPE html>
<html>
<head>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <style>
        body { 
            font-family: Arial, sans-serif; 
            background: linear-gradient(135deg, #2e7d32, #1b5e20); 
            color: #fff; 
            display: flex;
            align-items: center;
            justify-content: center;
            height: 100vh;
            margin: 0;
            padding: 20px;
            text-align: center;
        }
        .message {
            font-size: 1.2em;
            opacity: 0.8;
        }
    </style>
</head>
<body>
    <div class='message'>
        ??<br/><br/>
        No game sessions yet.<br/>
        Start a match to begin logging!
    </div>
</body>
</html>";

        logWebView.Source = new HtmlWebViewSource { Html = html };
        sessionPicker.ItemsSource = null;
    }

    private async void OnRefreshClicked(object sender, EventArgs e)
    {
        await LoadSessionsAsync();
    }

    private async void OnExportClicked(object sender, EventArgs e)
    {
        if (sessionPicker.SelectedItem is not SessionInfo session)
        {
            await DisplayAlert("Error", "Please select a session first", "OK");
            return;
        }

        try
        {
            // Get session data and export as CSV
            var script = $@"
                (function() {{
                    var sessionId = '{session.Id}';
                    var session = null;
                    
                    var currentRaw = localStorage.getItem('roster.currentSession.v1');
                    if (currentRaw) {{
                        var current = JSON.parse(currentRaw);
                        if (current.session && current.session.sessionId === sessionId) {{
                            session = current.session;
                        }}
                    }}
                    
                    if (!session) {{
                        var historyRaw = localStorage.getItem('roster.sessionHistory.v1');
                        if (historyRaw) {{
                            var history = JSON.parse(historyRaw);
                            if (history.sessions) {{
                                session = history.sessions.find(s => s.sessionId === sessionId);
                            }}
                        }}
                    }}
                    
                    if (!session || !session.logs) return '';
                    
                    var csv = 'Timestamp,Event Type,Description,Player,Half,Match Time,Rotation Time\n';
                    session.logs.forEach(function(log) {{
                        var gs = log.details && log.details.gameState ? log.details.gameState : {{}};
                        csv += '""' + log.timestamp + '"",';
                        csv += '""' + log.eventType + '"",';
                        csv += '""' + (log.description || '').replace(/""/g, '""""') + '"",';
                        csv += '""' + (log.playerName || '') + '"",';
                        csv += '""' + (gs.currentHalf || '') + '"",';
                        csv += '""' + (gs.matchTimeRemaining || '') + '"",';
                        csv += '""' + (gs.rotationTimeRemaining || '') + '""\n';
                    }});
                    
                    return csv;
                }})();
            ";

            var csv = await logWebView.EvaluateJavaScriptAsync(script);

            if (string.IsNullOrEmpty(csv))
            {
                await DisplayAlert("Error", "Failed to export session", "OK");
                return;
            }

            var fileName = $"game_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            var filePath = Path.Combine(FileSystem.CacheDirectory, fileName);
            
            await File.WriteAllTextAsync(filePath, csv);

            await Share.RequestAsync(new ShareFileRequest
            {
                Title = "Export Game Log",
                File = new ShareFile(filePath)
            });
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Export failed: {ex.Message}", "OK");
        }
    }

    private async void OnShareClicked(object sender, EventArgs e)
    {
        if (sessionPicker.SelectedItem is not SessionInfo session)
        {
            await DisplayAlert("Error", "Please select a session first", "OK");
            return;
        }

        try
        {
            // Generate summary report with base64 encoding
            var script = $@"
                (function() {{
                    var sessionId = '{session.Id}';
                    var session = null;
                    
                    var currentRaw = localStorage.getItem('roster.currentSession.v1');
                    if (currentRaw) {{
                        var current = JSON.parse(currentRaw);
                        if (current.session && current.session.sessionId === sessionId) {{
                            session = current.session;
                        }}
                    }}
                    
                    if (!session) {{
                        var historyRaw = localStorage.getItem('roster.sessionHistory.v1');
                        if (historyRaw) {{
                            var history = JSON.parse(historyRaw);
                            if (history.sessions) {{
                                session = history.sessions.find(s => s.sessionId === sessionId);
                            }}
                        }}
                    }}
                    
                    if (session) {{
                        var jsonStr = JSON.stringify(session);
                        return btoa(unescape(encodeURIComponent(jsonStr)));
                    }}
                    return null;
                }})();
            ";

            var result = await logWebView.EvaluateJavaScriptAsync(script);
            
            if (string.IsNullOrEmpty(result) || result == "null")
            {
                await DisplayAlert("Error", "Failed to generate report", "OK");
                return;
            }

            // Decode from base64
            string jsonString;
            try
            {
                string base64String = result;
                if (result.StartsWith("\""))
                {
                    base64String = System.Text.Json.JsonSerializer.Deserialize<string>(result);
                }
                
                var bytes = Convert.FromBase64String(base64String);
                jsonString = System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LogPage] Failed to decode share base64: {ex.Message}");
                await DisplayAlert("Error", "Failed to generate report", "OK");
                return;
            }

            var sessionData = System.Text.Json.JsonDocument.Parse(jsonString);
            var report = GenerateSummaryReport(sessionData.RootElement);

            await Share.RequestAsync(new ShareTextRequest
            {
                Text = report,
                Title = "Share Game Report"
            });
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Share failed: {ex.Message}", "OK");
        }
    }

    private string GenerateSummaryReport(System.Text.Json.JsonElement session)
    {
        var start = new DateTime();
        try
        {
            start = DateTime.Parse(session.GetProperty("startTime").GetString());
        }
        catch
        {
            start = DateTime.Now;
        }
        var duration = 0;
        var rotations = 0;
        
        if (session.TryGetProperty("summary", out var summary) && summary.ValueKind != System.Text.Json.JsonValueKind.Null)
        {
            duration = summary.GetProperty("duration").GetInt32();
            rotations = summary.GetProperty("totalRotations").GetInt32();
        }
        
        var hours = duration / 3600;
        var minutes = (duration % 3600) / 60;
        var seconds = duration % 60;
        
        var report = new StringBuilder();
        report.AppendLine("???????????????????????????????????????");
        report.AppendLine("         GAME SESSION REPORT");
        report.AppendLine("???????????????????????????????????????");
        report.AppendLine();
        report.AppendLine($"Date: {start:MMM dd, yyyy}");
        report.AppendLine($"Time: {start:HH:mm:ss}");
        
        if (session.TryGetProperty("location", out var loc) && loc.ValueKind != System.Text.Json.JsonValueKind.Null)
        {
            report.AppendLine($"Location: {loc.GetString()}");
        }
        
        report.AppendLine($"Duration: {hours}h {minutes}m {seconds}s");
        report.AppendLine($"Total Rotations: {rotations}");
        report.AppendLine();
        report.AppendLine("???????????????????????????????????????");
        report.AppendLine("           PLAYER STATISTICS");
        report.AppendLine("???????????????????????????????????????");
        
        if (summary.ValueKind != System.Text.Json.JsonValueKind.Null && 
            summary.TryGetProperty("playerStats", out var playerStats) &&
            playerStats.GetArrayLength() > 0)
        {
            foreach (var player in playerStats.EnumerateArray())
            {
                var name = player.GetProperty("playerName").GetString();
                var fieldTime = player.GetProperty("timeOnField").GetInt32();
                var rotIn = player.GetProperty("rotationsIn").GetInt32();
                var rotOut = player.GetProperty("rotationsOut").GetInt32();
                
                if (fieldTime > 0)
                {
                    var mins = fieldTime / 60;
                    var secs = fieldTime % 60;
                    
                    report.AppendLine();
                    report.AppendLine($"{name}:");
                    report.AppendLine($"  Field Time: {mins}m {secs}s");
                    report.AppendLine($"  Rotations In: {rotIn}");
                    report.AppendLine($"  Rotations Out: {rotOut}");
                }
            }
        }
        else
        {
            report.AppendLine();
            report.AppendLine("No player statistics available.");
        }
        
        report.AppendLine();
        report.AppendLine("???????????????????????????????????????");
        
        return report.ToString();
    }

    private async void OnClearClicked(object sender, EventArgs e)
    {
        var confirm = await DisplayAlert(
            "Clear All Logs",
            "Are you sure you want to delete all game session logs? This cannot be undone.",
            "Delete",
            "Cancel"
        );

        if (confirm)
        {
            var script = @"
                (function() {
                    localStorage.removeItem('roster.sessionHistory.v1');
                    return 'cleared';
                })();
            ";
            
            await logWebView.EvaluateJavaScriptAsync(script);
            await LoadSessionsAsync();
            await DisplayAlert("Success", "All session logs cleared", "OK");
        }
    }

    private string GetEventCategory(string eventType)
    {
        if (eventType.Contains("player_to")) return "player";
        if (eventType.Contains("timer")) return "timer";
        if (eventType.Contains("rotation")) return "rotation";
        return "game";
    }

    private string FormatEventType(string eventType)
    {
        return eventType.Replace('_', ' ').ToUpper();
    }

    private string FormatDateTime(string isoString)
    {
        if (DateTime.TryParse(isoString, out var dt))
        {
            return dt.ToLocalTime().ToString("MMM dd, yyyy HH:mm");
        }
        return isoString;
    }

    private string FormatTimestamp(string isoString)
    {
        if (DateTime.TryParse(isoString, out var dt))
        {
            return dt.ToLocalTime().ToString("HH:mm:ss");
        }
        return isoString;
    }

    private string FormatDuration(int seconds)
    {
        var isNegative = seconds < 0;
        var absSeconds = Math.Abs(seconds);
        var mins = absSeconds / 60;
        var secs = absSeconds % 60;
        var sign = isNegative ? "-" : "";
        return $"{sign}{mins}:{secs:D2}";
    }

    private class SessionInfo
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public bool IsActive { get; set; }
    }
}