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

#if WINDOWS
            // Windows: Fix version display (AppInfo returns assembly version, not display version)
            // Read from project file: ApplicationDisplayVersion = 1.0.3
            version = "1.0.3"; // TODO: Read dynamically from embedded resource
            return $"v{version} (Build {buildNumber}) | unknown | unknown";
#elif ANDROID || IOS || MACCATALYST
            // Get git commit hash and build timestamp from generated BuildInfo class
            // BuildInfo is generated at compile time for mobile platforms
            var gitCommit = BuildInfo.GitCommit;
            var buildTime = BuildInfo.BuildTime;

            // Format: v1.0.0 (Build 2) | abc123f | 2025-01-15 14:32 UTC
            return $"v{version} (Build {buildNumber}) | {gitCommit} | {buildTime}";
#else
            return $"v{version} (Build {buildNumber}) | unknown | unknown";
#endif
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
            border-bottom: 1px solid rgba(255,255,255,0.2);
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
        .badge-field    {{ background-color: #388e3c; color: #fff; }}
        .badge-bench    {{ background-color: #1565c0; color: #fff; }}
        .badge-goalie   {{ background-color: #f57f17; color: #fff; }}
        .badge-inactive {{ background-color: #424242; color: #ccc; }}
        .key {{
            display: inline-block;
            background: rgba(255,255,255,0.15);
            border-radius: 4px;
            padding: 1px 7px;
            font-size: 0.9em;
            font-family: monospace;
        }}
        .build-box {{
            text-align: center;
            background: rgba(0,0,0,0.3);
            padding: 10px;
            border-radius: 8px;
            margin-bottom: 15px;
        }}
    </style>
</head>
<body>

    <div class='build-box'>
        <div style='color:#bbb;font-size:0.85em;margin-bottom:3px;'>Current Build</div>
        <div style='color:#00d9ff;font-size:0.9em;font-family:monospace;word-break:break-all;'>{GetBuildDateTime()}</div>
    </div>

    <h1>⚽ Turf Timer Help 🥅</h1>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>👥 Player Roster</h3>
    <p>Each row shows:</p>
    <ul>
        <li><strong>☰</strong> drag handle (left edge)</li>
        <li><strong>Position icon</strong> — ⚽ Field &nbsp;|&nbsp; 🪑 Bench &nbsp;|&nbsp; 🥅 Goalie &nbsp;|&nbsp; ❌ Inactive</li>
        <li><strong>➤</strong> cyan arrow — this player is <em>next to rotate</em></li>
        <li><strong>Player name</strong></li>
        <li><strong>Field time</strong> (MM:SS) — cumulative time on field / as goalie (right edge)</li>
    </ul>
    <p>The row background colour matches the player's position:
        <span class='badge badge-field'>Field</span>
        <span class='badge badge-bench'>Bench</span>
        <span class='badge badge-goalie'>Goalie</span>
        <span class='badge badge-inactive'>Inactive</span>
    </p>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>👆 Gestures on a Player Row</h3>
    <ul>
        <li><strong>Tap:</strong>
            <ul>
                <li><em>Before game starts:</em> Opens rename dialog.</li>
                <li><em>During match:</em> Queues this player as the next to rotate in (bench) or out (field). The cyan ➤ arrow marks the queued player.</li>
            </ul>
        </li>
        <li><strong>Swipe left</strong> (→ left): cycles position <em>Field → Goalie</em>, or <em>Inactive → Field</em>.</li>
        <li><strong>Swipe right</strong> (→ right): cycles position <em>Field → Bench → Inactive</em>.</li>
        <li><strong>Long-press + drag</strong> (☰ handle): reorders the player in the rotation list. Works at any time, including during a live match.</li>
    </ul>


    <!-- ═══════════════════════════════════════════════════ -->
    <h3>🏆 Scores (Us / Them)</h3>
    <ul>
        <li>Scores appear in the header once the game starts.</li>
        <li><strong>Tap</strong> a score to <em>increment</em> (+1).</li>
        <li><strong>Double-tap</strong> a score to <em>decrement</em> (−1) if you made a mistake. Minimum is 0.</li>
        <li>Both the header score labels <em>and</em> the coloured side strips in Rotation view are tappable.</li>
    </ul>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>🎮 Bottom Buttons</h3>
    <ul>
        <li><strong>Start</strong> (left):
            <ul>
                <li>Starts or pauses the match timer.</li>
                <li>Shows <em>½ Time</em> at half-time — tap to begin the second half.</li>
                <li><em>Hold 1 second</em> to fully restart the game (resets timers and scores).</li>
            </ul>
        </li>
        <li><strong>Rotate</strong> (centre):
            <ul>
                <li>Executes the next rotation — swaps the selected number of field players off and bench players on.</li>
                <li>Resets the rotation countdown.</li>
                <li>You can rotate <em>any number of players at once</em>, from 1 up to the total number of bench players available.</li>
            </ul>
        </li>
        <li><strong>View: Rotation / View: Team</strong> (right):
            <ul>
                <li>Toggles between the player roster and the Rotation view.</li>
                <li>Label shows which view will appear when pressed.</li>
            </ul>
        </li>
    </ul>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>🔄 Rotation View</h3>
    <p>Press <span class='key'>View: Rotation</span> to see the substitution call-out screen:</p>
    <ul>
        <li><strong>Centre panel:</strong> Lists each upcoming swap — bench player coming <em>on</em> (blue, left-aligned) and field player going <em>off</em> (orange, right-aligned).</li>
        <li><strong>Left strip (green — Us):</strong> Shows your score. Tap to +1, double-tap to −1.</li>
        <li><strong>Right strip (red — Them):</strong> Shows opponent score. Tap to +1, double-tap to −1.</li>
        <li>Designed for sideline communication — large text, high contrast.</li>
    </ul>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>🔁 How Rotation Order Works</h3>
    <ul>
        <li>The app uses a <strong>FIFO queue</strong>: players who have been on the bench longest rotate on first; players who have been on the field longest rotate off first.</li>
        <li>Field time (MM:SS) on each row reflects this — higher time = next to come off.</li>
        <li><strong>Manual override:</strong> Tap a bench player during a match to move them to the front of the rotation-in queue. Tap a field player to move them to the front of the rotation-out queue. The cyan ➤ arrow shows the queued selection.</li>
        <li><strong>Reorder by dragging:</strong> Long-press and drag any row to permanently reposition the player in the rotation sequence.</li>
        <li>Manual queue selections are cleared after each rotation executes, returning to automatic FIFO.</li>
        <li><strong>Rotation alert:</strong> Before a rotation is due, a short vibration fires, giving you a heads-up before the rotation is due.</li>
    </ul>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>☁️ Shared (Cloud) Teams</h3>
    <ul>
        <li>Choose <strong>Shared</strong> on Team Details to create a cloud team, join with an invite code, or recover admin access on a new device.</li>
        <li>Choose <strong>Local</strong> for device-only teams (no cloud sync). Chat and Location tabs appear only for shared teams.</li>
        <li>👁️ <strong>View-only mode:</strong> If you joined as a team member (not admin), the amber banner shows and controls are disabled — the team admin runs the game.</li>
        <li>Roster and scores sync for shared teams so members can follow the match on their devices.</li>
    </ul>

    </body>
</html>";
    }
}