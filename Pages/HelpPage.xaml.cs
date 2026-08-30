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

    <h1>⚽ Turf Time Help 🥅</h1>
    <p style='text-align:center;color:#ccc;font-size:0.9em;'>Your sideline companion — Field View by default, fair rotations when you glance at the phone.</p>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>🏟️ Field View <em>(default Game screen)</em></h3>
    <p>This is the main screen for setting up and managing the match. Watch the pitch; use the phone only when you need to place someone or rotate.</p>
    <ul>
        <li><strong>4×4 pitch</strong> (cells 1–16, top-left = 1): one outfield player per cell. Drag or tap-to-place from Bench / Goalie / Absent.</li>
        <li><strong>Bench</strong> (right sideline): new-team players start here. Scroll; drag or tap onto the pitch, Goalie, or Absent. Stays visible during the match.</li>
        <li><strong>Goalie</strong> (over the goal): assign the keeper. A <strong>+</strong> appears on valid drop targets while dragging.</li>
        <li><strong>Absent</strong> (strip behind the goalie): park unavailable players. Move-only (no swap). Stays visible after Start for late arrivals.</li>
        <li><strong>Setup:</strong> tap a token to arm it, then tap a destination (cell / Bench / Goalie / Absent). Double-tap to rename.</li>
        <li><strong>Live match:</strong>
            <ul>
                <li><em>Field then Bench</em> — substitute (player leaves the pitch via the Bench).</li>
                <li><em>Bench then Field</em> (Manual basis) — seed a rotation pair; other bases use Bench taps for next-up queues.</li>
                <li><em>Bench then Absent</em> — injury / remove from lineup.</li>
                <li><em>Absent then Bench</em> — late arrival back into the pool.</li>
                <li>Live Field → Absent is blocked (leave via Bench first).</li>
            </ul>
        </li>
        <li>Tokens show a short name plus discreet <strong>MM:SS</strong> field time (Field / Goalie / Bench). Absent stays name-only.</li>
        <li>Coloured outlines mark who is next to rotate / paired. View-only shared members can watch but not drag.</li>
        <li>Optional yellow tip above the view explains the current Rotation Basis — hide it under <strong>Settings → Options → Information text</strong>.</li>
    </ul>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>🔁 Rotations <em>(core of the app)</em></h3>
    <ul>
        <li>Choose <strong>Settings → Options → Rotation Basis</strong>:
            <ul>
                <li><strong>Time Based</strong> (default) — most field time off; least on.</li>
                <li><strong>Sequential</strong> — roster-order FIFO after the last who rotated.</li>
                <li><strong>Position Based</strong> — cycles occupied Field View grid cells by row; Bench uses least time.</li>
                <li><strong>Manual</strong> — you seed pairs (Bench then Field); Rotate only runs those pairs, then clears. Countdown still reminds you.</li>
            </ul>
        </li>
        <li><strong>Rotate</strong> (bottom centre): tap to swap the queued pairs and reset the rotation countdown. Label shows the count (e.g. <span class='key'>Rotate 2</span>).</li>
        <li><strong>Hold Rotate ~1s:</strong> <em>Reset Clk</em> (restart countdown without swapping) or pick how many pairs to rotate.</li>
        <li>Matching outline colours link field ↔ bench next-up players on Field View and Team View.</li>
        <li>A short vibration warns before a rotation is due.</li>
        <li>On shared teams, only the match controller can rotate.</li>
    </ul>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>🔄 Rotation View <em>(glanceable sideline board)</em></h3>
    <p>Built so you can <strong>watch the game</strong> and only glance at the phone when telling the bench who is on and who is off — large type, high contrast, no squinting.</p>
    <ul>
        <li>Opens when you <strong>Start</strong> from setup, or via <span class='key'>View</span> → Rotation anytime.</li>
        <li><strong>Centre panel:</strong> each upcoming swap — bench coming <em>on</em> (blue) and field going <em>off</em> (orange). Pair count matches Rotate.</li>
        <li><strong>Tap the centre</strong> to execute the same rotation as the Rotate button (controller only on shared teams).</li>
        <li><strong>Hold ~1s</strong> on the centre (or Rotate) for Reset Clk / rotation count.</li>
        <li><strong>Green / red side strips:</strong> Us / Them scores — tap +1, double-tap −1 (controller only).</li>
    </ul>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>📋 Team View <em>(optional)</em></h3>
    <p>Legacy full roster list. <strong>Off by default</strong> — enable under <strong>Settings → Options → Enable Team View</strong> if you still want it in the View cycle.</p>
    <ul>
        <li>When enabled, View cycles <strong>Field → Team → Rotation</strong>.</li>
        <li>Each row: drag handle, position icon, next-to-rotate arrow, name, and field time (MM:SS).</li>
        <li>Row colour matches position:
            <span class='badge badge-field'>Field</span>
            <span class='badge badge-bench'>Bench</span>
            <span class='badge badge-goalie'>Goalie</span>
            <span class='badge badge-inactive'>Inactive / Absent</span>
        </li>
        <li><strong>Setup:</strong> tap a name to rename.</li>
        <li><strong>Live:</strong> tap to queue next-up / use the same Field↔Bench / Bench↔Absent rules as Field View.</li>
        <li><strong>Swipe left / right</strong> to cycle positions; <strong>long-press drag</strong> on ☰ to reorder (Sequential order).</li>
    </ul>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>🎮 Bottom Buttons &amp; Scores</h3>
    <ul>
        <li><strong>Start / Pause</strong> — match timer. At half-time shows <em>2nd Half</em>; mid-half pause shows Resume. Hold ~1s to restart / reset (releases shared match control).</li>
        <li><strong>Rotate</strong> — see Rotations above.</li>
        <li><strong>View</strong> — cycles <strong>Field ↔ Rotation</strong> by default (admins). Turn on <em>Enable Team View</em> in Options to insert the roster list. View-only devices stay on Field View.</li>
        <li><strong>Scores</strong> appear once the match is live — header labels and Rotation View side strips. Shared: controller only.</li>
    </ul>

    <!-- ═══════════════════════════════════════════════════ -->
    <h3>⚙️ Options</h3>
    <ul>
        <li><strong>Rotation Basis</strong> — how next-up players are chosen (see Rotations).</li>
        <li><strong>Information text</strong> — show or hide short Game-tab tips (e.g. the yellow rotation tip). Off = cleaner sideline display.</li>
        <li><strong>Enable Team View</strong> — off by default; when on, adds the legacy roster list to the View cycle.</li>
        <li><strong>Goal scorer &amp; assist</strong> — when logging goals, optionally pick scorer and assist for reports.</li>
        <li><strong>Match reminders</strong> — day before, morning, and time-to-leave from Details → Location schedule.</li>
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