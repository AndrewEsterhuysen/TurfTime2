namespace TurfTime2;

public partial class HelpPage : ContentPage
{
    public HelpPage()
    {
        InitializeComponent();

        // Load the help content directly
        helpWebView.Source = new HtmlWebViewSource
        {
            Html = GetHelpHtml()
        };
    }

    private string GetBuildDateTime()
    {
        try
        {
            var version = AppInfo.Current.VersionString;
            var buildNumber = AppInfo.Current.BuildString;

            // Get git commit hash and build timestamp from assembly metadata
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var attributes = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .Cast<System.Reflection.AssemblyMetadataAttribute>();

            var gitCommit = attributes.FirstOrDefault(a => a.Key == "GitCommit")?.Value ?? "unknown";
            var buildTime = attributes.FirstOrDefault(a => a.Key == "BuildTime")?.Value ?? "unknown";

            // Format: v1.0.0 (Build 2) | abc123f | 2025-01-15 14:32 UTC
            return $"v{version} (Build {buildNumber}) | {gitCommit} | {buildTime}";
        }
        catch
        {
            return "v1.0.0 (Build 2) | unknown";
        }
    }

    private string GetHelpHtml()
    {
        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <style>
        body {{
            font-family: Arial, sans-serif;
            background: linear-gradient(135deg, #2e7d32, #1b5e20);
            color: #fff;
            padding: 20px;
            margin: 0;
            line-height: 1.6;
        }}
        h1 {{
            color: #FF6B35;
            text-align: center;
            margin-bottom: 20px;
        }}
        h3 {{
            color: #FF6B35;
            margin-top: 20px;
            margin-bottom: 10px;
            border-bottom: 1px solid rgba(255, 255, 255, 0.2);
            padding-bottom: 5px;
        }}
        ul {{
            margin: 8px 0;
            padding-left: 20px;
        }}
        li {{
            margin-bottom: 8px;
        }}
        strong {{
            color: #fff;
        }}
        em {{
            color: #ffeb3b;
        }}
        .badge {{
            display: inline-block;
            padding: 2px 8px;
            border-radius: 4px;
            font-size: 0.85em;
            font-weight: bold;
            margin: 0 2px;
        }}
        .badge-field {{ background-color: #00e676; color: #222; }}
        .badge-bench {{ background-color: #8B4513; color: #fff; }}
        .badge-goalie {{ background-color: #ffeb3b; color: #222; }}
        .badge-inactive {{ background-color: #bdbdbd; color: #222; }}
    </style>
</head>
<body>
    <div style='text-align: center; background: rgba(0,0,0,0.3); padding: 10px; border-radius: 8px; margin-bottom: 15px;'>
        <div style='color: #bbb; font-size: 0.85em; margin-bottom: 3px;'>Current Build</div>
        <div style='color: #00d9ff; font-size: 0.9em; font-family: monospace; word-break: break-all;'>{GetBuildDateTime()}</div>
    </div>
    <h1>⚽ Turf Timer Help 🥅</h1>

    <h3>🎯 Quick Start</h3>
    <ul>
        <li><strong>Name Players:</strong> Tap player names to edit</li>
        <li><strong>Assign Positions:</strong> Check boxes or swipe cards:
            <ul>
                <li><span class='badge badge-field'>Field</span> <span class='badge badge-bench'>Bench</span> <span class='badge badge-goalie'>Goalie</span> <span class='badge badge-inactive'>Inactive</span></li>
            </ul>
        </li>
        <li><strong>Reorder:</strong> Long-press and drag to change rotation order <em>(anytime, even during match)</em></li>
    </ul>

    <h3>⏱️ Timers</h3>
    <ul>
        <li><strong>Match Time:</strong> Tap to set game duration (default: 90 min)
            <ul><li>Splits into two halves automatically</li></ul>
        </li>
        <li><strong>Rotate Time:</strong> Tap to set rotation countdown (default: 2:00)
            <ul>
                <li><strong>Auto Button:</strong> Calculates optimal rotation time
                    <ul>
                        <li><em>Equal Time Formula:</em> (Match Duration × Rotate Count) ÷ Bench Players</li>
                        <li><em>Fast Fives Formula:</em> Ensures minimum 5 rotations/half, adapts to bench size</li>
                        <li>Uses whichever gives <em>more frequent</em> rotations</li>
                    </ul>
                </li>
            </ul>
        </li>
    </ul>

    <h3>🎮 Controls</h3>
    <ul>
        <li><strong>Start/Pause:</strong> Begin or pause match timer
            <ul><li><em>Hold 1 sec</em> to restart entire game</li></ul>
        </li>
        <li><strong>Rotate:</strong> Swap field/bench players (highlighted players rotate)
            <ul>
                <li><em>Hold 0.5 sec</em> to change rotation count (1, 2, 3+ players per rotation)</li>
                <li>Resets rotation countdown</li>
            </ul>
        </li>
        <li><strong>VIEW:</strong> Cycle between preferred view and Rotation Display (VIEW_C)
            <ul>
                <li><em>Set preference in Settings → Team View</em></li>
            </ul>
        </li>
    </ul>

    <h3>👀 View Modes</h3>
    <ul>
        <li><strong>Swipe View:</strong> Touch-friendly swipeable player cards
            <ul>
                <li><em>Swipe left:</em> Field → Goalie (or Inactive → Field)</li>
                <li><em>Swipe right:</em> Field → Bench → Inactive</li>
                <li><em>Tap:</em> Set as next to rotate (after game starts)</li>
                <li><em>Long-press:</em> Drag to reorder</li>
                <li><em>Inactive Toggle:</em> Show/hide inactive players (after game starts)</li>
            </ul>
        </li>
        <li><strong>Table View:</strong> Traditional table with checkboxes
            <ul>
                <li><em>Tap name:</em> Set as next to rotate (after game starts)</li>
                <li><em>Inactive Toggle:</em> Show/hide inactive players (after game starts)</li>
            </ul>
        </li>
        <li><strong>Rotation Display (VIEW_C):</strong> Call-off view for substitutions
            <ul>
                <li>Large names in alternating bench/field pattern</li>
                <li>Perfect for sideline communication</li>
                <li>Color-coded: bench (brown), field (green)</li>
                <li>Score buttons on sides</li>
            </ul>
        </li>
    </ul>

    <h3>🎨 Settings</h3>
    <ul>
        <li><strong>Log:</strong> View game history, export CSV, share summary</li>
        <li><strong>Skins:</strong> Choose Classic (warm tones) or Modern (high contrast) theme</li>
        <li><strong>Rotation Style:</strong> Customize next-player highlighting:
            <ul>
                <li>Glowing border with pulse (default)</li>
                <li>Bouncing arrow indicator</li>
            </ul>
        </li>
        <li><strong>Team View:</strong> Set default view (Swipe or Table)
            <ul><li>Affects app startup and VIEW button behavior</li></ul>
        </li>
    </ul>

    <h3>📊 During Match</h3>
    <ul>
        <li><strong>Player Timers:</strong> Show field/goalie time in MM:SS</li>
        <li><strong>Next Player Highlight:</strong> Rotation style marks players scheduled to rotate</li>
        <li><strong>Inactive Players:</strong> Auto-hide after game starts
            <ul><li>Tap toggle button to show/hide</li>
            <li>Visible players automatically enlarge to fill space</li></ul>
        </li>
        <li><strong>Alerts:</strong> Vibration pulse at 10 sec, continuous when due, yellow flash at zero</li>
        <li><strong>Half-Time:</strong> Button shows '1/2 Time' - tap to start second half</li>
        <li><strong>Scores:</strong> Displayed in header (Us vs Them) during match</li>
    </ul>

    <h3>📝 Editing Rules</h3>
    <ul>
        <li><strong>Before Start:</strong> Everything editable</li>
        <li><strong>During Match:</strong>
            <ul>
                <li>✅ Position changes, rotation order, timers, inactive status</li>
                <li>❌ Player names (Swipe View), match time</li>
                <li>Table View: ✅ Names clickable to set next rotation</li>
            </ul>
        </li>
    </ul>

    <h3>💡 Tips</h3>
    <ul>
        <li>💾 Setup auto-saves between sessions</li>
        <li>👤 One goalie max - checking new goalie unchecks previous</li>
        <li>🎯 Tap/click player during match to set as next to rotate</li>
        <li>📱 Buttons stay fixed at bottom, content scrolls independently</li>
        <li>📈 Player counters show <em>total</em> field time, not current stint</li>
        <li>🔄 Dragging reorders rotation queue instantly</li>
    </ul>
</body>
</html>";
    }
}