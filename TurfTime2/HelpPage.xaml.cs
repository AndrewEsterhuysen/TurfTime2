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
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var buildDate = new DateTime(2000, 1, 1).AddDays(assembly.GetName().Version.Build).AddSeconds(assembly.GetName().Version.Revision * 2);

            // For more accurate build time, use file modification time of the assembly
            var assemblyPath = assembly.Location;
            if (!string.IsNullOrEmpty(assemblyPath) && File.Exists(assemblyPath))
            {
                var fileInfo = new FileInfo(assemblyPath);
                return fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
            }

            // Fallback to version-based calculation
            return buildDate.ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch
        {
            // If we can't get the build time, return the version instead
            var version = AppInfo.Current.VersionString;
            var buildNumber = AppInfo.Current.BuildString;
            return $"v{version} (Build {buildNumber})";
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
    <p style=""text-align: center; color: #bbb; font-size: 0.9em;"">Build: {GetBuildDateTime()}</p>
    <h1>? Turf Timer Help ??</h1>
    
    <h3>Getting Started</h3>
    <ul>
        <li><strong>Name Players:</strong> Click on 'Player 1', 'Player 2', etc. to edit names</li>
        <li><strong>Assign Positions:</strong> Check boxes to assign each player:
            <ul>
                <li><span class='badge badge-field'>Field</span> - Currently playing on field</li>
                <li><span class='badge badge-bench'>Bench</span> - Waiting on bench</li>
                <li><span class='badge badge-goalie'>Goalie</span> - Playing as goalkeeper</li>
                <li><span class='badge badge-inactive'>Inactive</span> - Not playing today</li>
            </ul>
        </li>
        <li><strong>Drag to Reorder:</strong> Click and drag any player row to change rotation order <em>(works anytime, even during match)</em></li>
    </ul>

    <h3>Timers</h3>
    <ul>
        <li><strong>Match Time:</strong> Click to set game duration (default: 90 min). Timer splits into two halves automatically</li>
        <li><strong>Rotate Time:</strong> Click to set rotation countdown (default: 2:00). Timer alerts you when substitution is due
            <ul>
                <li><strong>Auto Button:</strong> Automatically calculates the optimal rotation time using a smart hybrid formula
                    <ul>
                        <li><strong>Formula 1 - Equal Playing Time:</strong> (Match Duration × Rotation Count) ÷ Bench Players</li>
                        <li><strong>Formula 2 - Fast Fives Mode (Bench-Aware):</strong> (Half Duration × Rotation Count) ÷ max(5, Bench Players ÷ Rotation Count)
                            <ul>
                                <li>Ensures minimum 5 rotations per half for fast games</li>
                                <li>BUT increases frequency if needed to cycle through all bench players</li>
                                <li>Adapts to bench size automatically</li>
                            </ul>
                        </li>
                        <li>Uses the SMALLER value (more frequent rotations) to ensure both speed and fairness</li>
                        <li>Example (40-min fast fives, 2 bench, rotate 1):
                            <ul>
                                <li>Formula 1: (2400 × 1) ÷ 2 = 1200 sec = 20 min</li>
                                <li>Formula 2: (1200 × 1) ÷ max(5, 2) = 240 sec = 4 min ✓ (fast rotations)</li>
                                <li>Each player gets 5 rotations = 20 min field time ✓</li>
                            </ul>
                        </li>
                        <li>Example (40-min fast fives, 10 bench, rotate 1):
                            <ul>
                                <li>Formula 1: (2400 × 1) ÷ 10 = 240 sec = 4 min</li>
                                <li>Formula 2: (1200 × 1) ÷ max(5, 10) = 120 sec = 2 min ✓ (adapts to bench size)</li>
                                <li>Each player gets 10 rotations = 20 min field time ✓</li>
                            </ul>
                        </li>
                        <li>Example (90-min match, 6 bench, rotate 2):
                            <ul>
                                <li>Formula 1: (5400 × 2) ÷ 6 = 1800 sec = 30 min ✓ (respects bench size)</li>
                                <li>Formula 2: (2700 × 2) ÷ max(5, 3) = 1080 sec = 18 min</li>
                            </ul>
                        </li>
                    </ul>
                </li>
            </ul>
        </li>
    </ul>

    <h3>Buttons</h3>
    <ul>
        <li><strong>Start/Pause:</strong> Begin or pause the match timer
            <ul>
                <li>Players are locked and auto-arranged by position when started</li>
                <li><em>Hold for 1 second</em> to restart the entire game</li>
            </ul>
        </li>
        <li><strong>Rotate:</strong> Swap next field player(s) with next bench player(s)
            <ul>
                <li>Red highlighted rows show who rotates next</li>
                <li>Resets the rotation countdown timer</li>
                <li><em>Hold for 0.5 seconds</em> to change rotation count (default: 1 player)
                    <ul>
                        <li>Use ▲/▼ arrows to adjust how many players rotate each time</li>
                        <li>Maximum is limited by number of bench players available</li>
                        <li>Button text shows 'Rotate 1', 'Rotate 2', etc.</li>
                        <li>All players to be rotated are highlighted in red</li>
                    </ul>
                </li>
            </ul>
        </li>
        <li><strong>View Modes (View_A/B/C/D):</strong> Toggle between four different display modes to optimize screen space and rotation clarity
            <ul>
                <li><strong>View_A (All Players):</strong> Shows all team slots with normal row spacing - use this for full team overview and managing inactive players</li>
                <li><strong>View_B (Active Only):</strong> Hides inactive players and enlarges rows to fill the screen - perfect for focusing on active roster during setup</li>
                <li><strong>View_C (Rotation Focus):</strong> Shows only bench players and the matching number of field players who will rotate next - maximum simplicity for quick substitutions during fast-paced games</li>
                <li><strong>View_D (Call Off View):</strong> Shows ONLY the players about to rotate in alternating left/right pattern - bench players can easily see which field player to call off
                    <ul>
                        <li>Displays rotation count × 2 names (e.g., if Rotate 2, shows 4 names)</li>
                        <li>Pattern: Bench player (left) → Field player (right) → Bench player (left) → Field player (right)</li>
                        <li>Large text, color-coded (bench=brown/orange, field=green)</li>
                        <li>Perfect for sideline communication: bench players call their corresponding field player names</li>
                    </ul>
                </li>
            </ul>
        </li>
    </ul>

    <h3>During the Match</h3>
    <ul>
        <li><strong>Player Timers:</strong> Shows how long each field/goalie player has been on</li>
        <li><strong>Red Background:</strong> All players scheduled for next rotation are highlighted with red background and outline
            <ul>
                <li>Number of highlighted players matches rotation count (e.g., if 'Rotate 3', six rows are red: 3 field + 3 bench)</li>
                <li>Highlighting updates immediately when rotation count changes</li>
            </ul>
        </li>
        <li><strong>Drag to Reorder:</strong> You can still drag players to adjust rotation order during the match</li>
        <li><strong>Vibration Alerts:</strong>
            <ul>
                <li>Short pulse at 10 seconds before rotation</li>
                <li>Continuous vibration when rotation is due</li>
                <li>Yellow screen flash when rotation timer reaches zero</li>
            </ul>
        </li>
        <li><strong>Half-Time:</strong> Button shows '1/2 Time' when first half ends. Click to start second half</li>
    </ul>

    <h3>Settings Menu</h3>
    <ul>
        <li><strong>Navigation:</strong> Tap Settings from the main menu to access app configuration options</li>
        <li><strong>Log:</strong> Access game session logs, export data, and review event history (moved from main menu for better organization)</li>
        <li><strong>Skins:</strong> Customize the app's appearance with different themes</li>
    </ul>

    <h3>Skins & Themes</h3>
    <ul>
        <li><strong>Classic Theme:</strong> Original green soccer field colors with warm tones</li>
        <li><strong>Modern Theme:</strong> Dark theme with high contrast, brighter accent colors, and enhanced borders for better visibility</li>
        <li><strong>Theme Selection:</strong> Tap any theme in Settings > Skins to apply it instantly</li>
        <li><strong>Persistence:</strong> Your theme preference is saved and applied automatically when you open the app</li>
        <li><strong>Preview Colors:</strong> Each theme shows color swatches so you can see the palette before selecting</li>
    </ul>

    <h3>Log Menu</h3>
    <ul>
        <li><strong>Automatic Logging:</strong> Every action is tracked automatically - player changes, rotations, timer adjustments, game events</li>
        <li><strong>View History:</strong> Select current game or past sessions from dropdown to review what happened</li>
        <li><strong>Latest First:</strong> Newest events appear at the top for easy review</li>
        <li><strong>Export Data:</strong> Tap 'Export' to save session as CSV file for spreadsheet analysis</li>
        <li><strong>Share Report:</strong> Tap 'Share' to send game summary with player statistics via text/email</li>
        <li><strong>Color-Coded Events:</strong>
            <ul>
                <li>? Green - Player position changes</li>
                <li>? Yellow - Timer adjustments</li>
                <li>? Orange - Game state events</li>
                <li>? Brown - Rotations executed</li>
            </ul>
        </li>
        <li><strong>Game Context:</strong> Each event shows match time, rotation time, and current half</li>
        <li><strong>Clear History:</strong> Tap 'Clear All' to delete all logs (confirmation required)</li>
    </ul>

    <h3>Editing Restrictions</h3>
    <ul>
        <li><strong>Before Start:</strong> Everything is editable</li>
        <li><strong>During Match:</strong>
            <ul>
                <li>? Player names are locked</li>
                <li>? Match time is locked</li>
                <li>? Position checkboxes can be changed</li>
                <li>? Rotation countdown can be adjusted</li>
                <li>? Inactive players can still be edited</li>
                <li>? Players can be dragged to reorder rotation</li>
            </ul>
        </li>
        <li><strong>After Restart:</strong> All editing is restored</li>
    </ul>

    <h3>Tips & Tricks</h3>
    <ul>
        <li>?? Click a player's name during a match to manually select them as next to rotate</li>
        <li>?? One goalie maximum - checking a new goalie automatically unchecks the previous one</li>
        <li>?? Your setup auto-saves and persists between sessions</li>
        <li>?? Drag players anytime to adjust rotation order on the fly</li>
        <li>?? Player counters show total field time, not just current stint</li>
        <li>?? Dragging resets rotation pointers - next players are recalculated from new positions</li>
        <li>?? Set rotation count to match your strategy - rotate 1 player for precision, or 3-4 players for full line changes</li>
        <li>?? All players in next rotation are highlighted - makes it easy to prepare substitutions in advance</li>
    </ul>
</body>
</html>";
    }
}