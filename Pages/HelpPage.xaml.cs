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

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>✉️ Feature Requests &amp; Bug Reports</h3>
    <p>Found a bug or have an idea that would help on the sideline? Email:</p>
    <p style='text-align:center;margin:12px 0;'>
        <a href='mailto:andrew.esterhuysen00@gmail.com'
           style='color:#00d9ff;font-weight:bold;font-size:1.05em;word-break:break-all;'>
            andrew.esterhuysen00@gmail.com
        </a>
    </p>
    <p style='color:#ccc;font-size:0.9em;'>Include your device type (iPhone / Android), app version from the build box above, and steps to reproduce when reporting a bug.</p>

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
        <li><strong>Shared teams:</strong> only the <em>match controller</em> can change scores. View-only devices show scores but ignore taps.</li>
    </ul>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>🎮 Bottom Buttons</h3>
    <ul>
        <li><strong>Start</strong> (left):
            <ul>
                <li>Starts or pauses the match timer.</li>
                <li>When you <em>Start</em> from setup (kickoff), the app automatically switches to the <strong>Rotation</strong> view so the next swaps are visible immediately.</li>
                <li>Shows <em>½ Time</em> at half-time — tap to begin the second half.</li>
                <li><em>Hold 1 second</em> to fully restart / reset the game (resets timers and scores; releases match control on shared teams).</li>
            </ul>
        </li>
        <li><strong>Rotate</strong> (centre):
            <ul>
                <li><strong>Tap</strong> executes the next rotation — swaps the selected number of field players off and bench players on.</li>
                <li>Resets the rotation countdown.</li>
                <li>Button label shows the current count, e.g. <span class='key'>Rotate 2</span>.</li>
                <li><em>Hold ~1 second</em> to open the <strong>rotation count</strong> selector (1 up to the number of bench players). Choosing a new count fully re-seeds the next-up FIFO queues from automatic order (manual queue picks are not kept).</li>
                <li>Controller only on shared teams (view-only devices cannot rotate or change the count).</li>
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
    <p>Opens automatically when you Start a match from setup, or press <span class='key'>View: Rotation</span> anytime:</p>
    <ul>
        <li><strong>Centre panel:</strong> Lists each upcoming swap — bench player coming <em>on</em> (blue, left-aligned) and field player going <em>off</em> (orange, right-aligned). How many pairs appear matches the rotation count.</li>
        <li><strong>Tap to Rotate:</strong> Tap the centre panel to execute the same rotation as the bottom <span class='key'>Rotate</span> button (controller only on shared teams).</li>
        <li><strong>Hold ~1 second</strong> on the centre panel (or on <span class='key'>Rotate</span>) to choose how many players to rotate (1 … max on the bench). Same full FIFO reseed as holding Rotate.</li>
        <li><strong>Left strip (green — Us):</strong> Shows your score. Tap to +1, double-tap to −1 (controller only on shared teams).</li>
        <li><strong>Right strip (red — Them):</strong> Shows opponent score. Tap to +1, double-tap to −1 (controller only on shared teams).</li>
        <li>Designed for sideline communication — large text, high contrast.</li>
    </ul>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>🔁 How Rotation Order Works</h3>
    <ul>
        <li>The app uses a <strong>FIFO queue</strong>: players who have been on the bench longest rotate on first; players who have been on the field longest rotate off first.</li>
        <li>Field time (MM:SS) on each row reflects this — higher time = next to come off.</li>
        <li><strong>Manual override (Team view):</strong> During a match, tap a bench player to queue them on, or a field player to queue them off. Queues grow/shrink and the rotation count follows. The cyan ➤ arrow marks queued players.</li>
        <li><strong>Rotation count (Rotate / Tap to Rotate hold):</strong> Sets how many pairs will swap. Changing the count re-seeds both next-up queues from automatic FIFO (does not keep a prior manual queue order).</li>
        <li>Team-view taps and long-press count selection share the same queues — the last action you take wins until you change it again.</li>
        <li><strong>Reorder by dragging:</strong> Long-press and drag any row to permanently reposition the player in the rotation sequence.</li>
        <li>After each rotation executes, queues are re-seeded for the next cycle.</li>
        <li><strong>Rotation alert:</strong> Before a rotation is due, a short vibration fires, giving you a heads-up before the rotation is due.</li>
    </ul>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>👤 Your Display Name</h3>
    <ul>
        <li>On first launch (or whenever no name is set), Turf Time asks for a <strong>display name</strong> before you continue.</li>
        <li>Used in Chat, push notifications, member lists, and when you join a shared team.</li>
        <li>Edit anytime under Team Details → Current Team.</li>
    </ul>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>☁️ Shared (Cloud) Teams</h3>
    <ul>
        <li>Choose <strong>Shared</strong> on Team Details to create a cloud team, join with an invite code, or recover admin access on a new device.</li>
        <li>Choose <strong>Local</strong> for device-only teams (no cloud sync). Chat and Details tabs appear only for shared teams.</li>
        <li>Set a <strong>display name</strong> when you create or join a shared team (editable under Current Team). That name appears in Chat, push notifications, and the member list — not a device ID.</li>
        <li>Roster, timers, and scores sync so everyone can follow the match on their devices.</li>
        <li><strong>Share invite (QR + link):</strong> From Team Details, share a QR image that includes a short “press and hold” note under the code, plus a <code>turf://v1/join?invite=…</code> link for email or SMS. The receiver needs Turf Time installed; tapping the link (or opening the QR) starts join as a Member.</li>
        <li>Local team QR import is still scan/photo via Import Team (full roster offline copy).</li>
    </ul>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>👑 Roles: Owner · Admin · Member</h3>
    <ul>
        <li><strong>Owner</strong> — the club manager who created the team (or received ownership). Can transfer ownership, delete the team from Firebase, remove other Admins, and do everything an Admin can.</li>
        <li><strong>Admin</strong> — can run games, edit Location / Kit / Duties, promote members, remove Members, and manage invite codes (not delete the whole team).</li>
        <li><strong>Member</strong> — view-only on the Game tab; can follow roster, timers, and scores; can use Chat.</li>
        <li>Open <strong>Team Admin Panel → View Team Members</strong> to see who is Owner, Admin, or Member.</li>
    </ul>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>🎮 Single Match Controller (Shared Teams)</h3>
    <p>Only <strong>one Admin at a time</strong> may control a live match (timers, rotate, scores, roster edits). This prevents two devices from fighting over the same game.</p>
    <ul>
        <li><strong>Start</strong> on a free Admin device claims control and publishes it to the cloud.</li>
        <li>👁️ <strong>Yellow banner — Member:</strong> “VIEW-ONLY MODE — Team Admin controls the game.”</li>
        <li>👁️ <strong>Yellow banner — locked co-Admin:</strong> “&#123;Name&#125; started game · Request control” — tap to ask the controller to hand over. They get Accept / Reject on their Game tab.</li>
        <li>👁️ <strong>Vacant control:</strong> after Relinquish or server auto-release (~90s offline), Admins see “No controller · Tap to take control.”</li>
        <li><strong>Watch Only</strong> (grey button in Setup / Finished): voluntary view-only on this device without demoting your Admin role. Tap again for <em>Take Control</em> when no one holds the seat.</li>
        <li><strong>Relinquish Match Control</strong> (Team Admin Panel): free the seat so another Admin can take over without ending the match.</li>
        <li>After a full <strong>Reset</strong>, control is released and any Admin may Start the next game.</li>
    </ul>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>🛠️ Team Admin Panel</h3>
    <ul>
        <li><strong>Invite Code</strong> — share so others can join.</li>
        <li><strong>View Team Members</strong> — names with (Owner) / (Admin) / (Member).</li>
        <li><strong>Promote to Admin</strong> — elevate a Member (they should open Game so the new role applies).</li>
        <li><strong>Remove Member</strong> — delete someone from the cloud team. Owner can remove other Admins; Admins can remove Members. Use <em>Leave Team</em> to remove yourself.</li>
        <li><strong>Relinquish Match Control</strong> — hand over the live controller seat.</li>
        <li><strong>Transfer Ownership</strong> (Owner only) — pass club ownership to another Admin.</li>
        <li><strong>Regenerate Invite Code</strong> — invalidate the old code and issue a new one.</li>
        <li><strong>Delete team</strong> (swipe under Change Team, Owner only) — permanently removes the team from Firebase for everyone.</li>
    </ul>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>📍 Details: Location · Kit · Duties</h3>
    <ul>
        <li><strong>Location</strong> — match date/time, arrive time, venue name, coordinates, and maps link. On shared teams this schedule syncs; it also drives local match reminders.</li>
        <li><strong>Kit</strong> — arrive / warm-up / game / departure kit notes and special events.</li>
        <li><strong>Duties</strong> — duty officer, canteen, grounds setup / pack-up, other notes.</li>
        <li>Admins edit; Members see a view-only banner on these pages.</li>
    </ul>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>⏰ Match Reminder Notifications</h3>
    <p>Optional <strong>device-local</strong> reminders so you don’t miss match day. They are <em>not</em> system Clock alarms and are separate from team chat push alerts.</p>
    <ul>
        <li><strong>Where to enable:</strong> Settings → Options → <em>Match reminders</em>.</li>
        <li><strong>What they use:</strong> the schedule on Details → Location (kickoff, arrive time, venue). On shared teams, schedule updates reschedule reminders automatically.</li>
        <li><strong>Day before</strong> — evening before kickoff (default 6&nbsp;pm local).</li>
        <li><strong>Morning of match</strong> — morning on match day (default 7&nbsp;am local).</li>
        <li><strong>Time to leave</strong> — fires before arrive time using your leave buffer (30 / 45 / 60 / 90 minutes).</li>
        <li>One master switch can enable all three kinds; you can turn individual kinds on or off.</li>
        <li>Allow notification permission in system Settings when prompted, or reminders cannot fire.</li>
        <li>Reminders follow the <em>current team</em> on this device. Switch teams or change the schedule to refresh them.</li>
    </ul>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>🔔 Team Chat Push Alerts</h3>
    <ul>
        <li>On shared teams, new Chat messages can raise a push / local notification when the app is in the background (and banners when allowed in the foreground).</li>
        <li>Requires notification permission. Distinct from match reminders above.</li>
    </ul>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>💡 Tips</h3>
    <ul>
        <li>Keep the <strong>Game</strong> tab open on the controller’s device when another Admin requests control.</li>
        <li>If you reinstall often during testing, Firebase may create a <em>new device identity</em> — the cloud Owner is still the original <code>createdBy</code> account. Prefer the original Owner device for delete / transfer.</li>
        <li>Open <strong>Help</strong> anytime from the side menu for this guide.</li>
    </ul>

    </body>
</html>";
    }
}