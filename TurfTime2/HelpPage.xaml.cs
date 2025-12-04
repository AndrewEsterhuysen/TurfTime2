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
    <p style=""text-align: center; color: #bbb; font-size: 0.9em;"">Build Date/Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>
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
        <li><strong>Rotate Time:</strong> Click to set rotation countdown (default: 2:00). Timer alerts you when substitution is due</li>
    </ul>

    <h3>Buttons</h3>
    <ul>
        <li><strong>Start/Pause:</strong> Begin or pause the match timer
            <ul>
                <li>Players are locked and auto-arranged by position when started</li>
                <li><em>Hold for 1 second</em> to restart the entire game</li>
            </ul>
        </li>
        <li><strong>Rotate:</strong> Swap next field player with next bench player
            <ul>
                <li>Red highlighted rows show who rotates next</li>
                <li>Resets the rotation countdown timer</li>
            </ul>
        </li>
        <li><strong>ZOOM:</strong> Toggle between normal and fullscreen view
            <ul>
                <li>Hides inactive players for clearer view during game</li>
                <li>Automatically sizes rows to fill screen</li>
            </ul>
        </li>
    </ul>

    <h3>During the Match</h3>
    <ul>
        <li><strong>Player Timers:</strong> Shows how long each field/goalie player has been on</li>
        <li><strong>Red Background:</strong> Players with red backgrounds rotate next</li>
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
    </ul>
</body>
</html>";
    }
}