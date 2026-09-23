namespace TurfTime2;

public partial class HelpPage : ContentPage
{
    public HelpPage()
    {
        InitializeComponent();

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
            version = "1.0.3";
            return $"v{version} (Build {buildNumber}) | unknown | unknown";
#elif ANDROID || IOS || MACCATALYST
            var gitCommit = BuildInfo.GitCommit;
            var buildTime = BuildInfo.BuildTime;
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
            font-family: -apple-system, BlinkMacSystemFont, Arial, sans-serif;
            background: linear-gradient(135deg, #2e7d32, #1b5e20);
            color: #fff;
            padding: 16px 14px 28px;
            margin: 0;
            line-height: 1.55;
        }}
        h1 {{
            color: #FF6B35;
            text-align: center;
            margin: 8px 0 6px;
            font-size: 1.45em;
        }}
        .tagline {{
            text-align: center;
            color: #ccc;
            font-size: 0.9em;
            margin: 0 0 14px;
        }}
        .build-box {{
            text-align: center;
            background: rgba(0,0,0,0.3);
            padding: 10px;
            border-radius: 8px;
            margin-bottom: 12px;
        }}
        .contact {{
            background: rgba(0,0,0,0.22);
            border-radius: 10px;
            padding: 12px 14px;
            margin-bottom: 14px;
            border: 1px solid rgba(255,255,255,0.12);
        }}
        .contact a {{
            color: #00d9ff;
            font-weight: bold;
            word-break: break-all;
        }}
        .hint {{
            color: #bbb;
            font-size: 0.88em;
            text-align: center;
            margin: 0 0 12px;
        }}
        details.topic {{
            background: rgba(0,0,0,0.28);
            border: 1px solid rgba(255,255,255,0.14);
            border-radius: 10px;
            margin-bottom: 10px;
            overflow: hidden;
        }}
        details.topic[open] {{
            border-color: rgba(255,107,53,0.55);
            background: rgba(0,0,0,0.36);
        }}
        summary.topic-head {{
            list-style: none;
            cursor: pointer;
            padding: 12px 14px;
            display: flex;
            flex-direction: column;
            gap: 4px;
            -webkit-tap-highlight-color: transparent;
        }}
        summary.topic-head::-webkit-details-marker {{ display: none; }}
        .topic-title {{
            color: #FF6B35;
            font-weight: bold;
            font-size: 1.02em;
            display: flex;
            align-items: center;
            justify-content: space-between;
            gap: 8px;
        }}
        .chevron {{
            color: #FF6B35;
            font-size: 0.85em;
            flex-shrink: 0;
        }}
        details.topic[open] .chevron {{
            transform: rotate(180deg);
        }}
        .topic-blurb {{
            color: #c8c8c8;
            font-size: 0.86em;
            font-weight: normal;
            line-height: 1.35;
            padding-right: 18px;
        }}
        .topic-body {{
            padding: 0 14px 14px;
            border-top: 1px solid rgba(255,255,255,0.1);
            margin-top: 0;
            padding-top: 10px;
        }}
        .topic-body p {{ margin: 0 0 8px; }}
        ul {{
            margin: 6px 0;
            padding-left: 18px;
        }}
        li {{ margin-bottom: 7px; }}
        strong {{ color: #fff; }}
        em {{ color: #ffeb3b; }}
        code {{
            background: rgba(255,255,255,0.12);
            padding: 1px 5px;
            border-radius: 3px;
            font-size: 0.92em;
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
    </style>
</head>
<body>

    <div class='build-box'>
        <div style='color:#bbb;font-size:0.85em;margin-bottom:3px;'>Current Build</div>
        <div style='color:#00d9ff;font-size:0.9em;font-family:monospace;word-break:break-all;'>{GetBuildDateTime()}</div>
    </div>

    <h1>⚽ Turf Time Help 🥅</h1>
    <p class='tagline'>Your sideline companion — Field View by default, fair rotations when you glance at the phone.</p>
    <p class='hint'>Tap a topic below to expand. Only open what you need.</p>

    <div class='contact'>
        <strong>✉️ Feature requests &amp; bugs</strong>
        <p style='margin:6px 0 4px;font-size:0.92em;'>Email
            <a href='mailto:andrew.esterhuysen00@gmail.com'>andrew.esterhuysen00@gmail.com</a>
        </p>
        <p style='margin:0;color:#ccc;font-size:0.85em;'>Include device type, the build box above, and steps to reproduce.</p>
    </div>

    <!-- ── Game play ── -->
    <details class='topic'>
        <summary class='topic-head'>
            <span class='topic-title'>🏟️ Field View <span class='chevron'>▼</span></span>
            <span class='topic-blurb'>Default Game screen — pitch, Bench, Goalie, Absent, live subs</span>
        </summary>
        <div class='topic-body'>
            <p>Main screen for setup and the match. Watch the pitch; use the phone when placing or rotating.</p>
            <ul>
                <li><strong>4×4 pitch</strong> (cells 1–16): one outfield player per cell. Drag or tap-to-place from Bench / Goalie / Absent.</li>
                <li><strong>Bench</strong> (right): new players start here. Drag or tap onto pitch, Goalie, or Absent. Stays visible during the match.</li>
                <li><strong>Goalie</strong> (over the goal): assign the keeper. A <strong>+</strong> appears on valid drop targets while dragging.</li>
                <li><strong>Absent</strong> (behind goalie): unavailable players. Move-only; stays visible after Start for late arrivals.</li>
                <li><strong>Setup:</strong> tap a token to arm it, then tap a destination. Double-tap to rename.</li>
                <li><strong>Live:</strong> Field→Bench = sub out; Bench→Field seeds/queues next-up (depends on Rotation Basis); Bench→Absent = injury; Absent→Bench = late arrival. Live Field→Absent is blocked.</li>
                <li>Tokens show short name + discreet <strong>MM:SS</strong> (Field / Goalie / Bench). Outlines mark next-up / pairs.</li>
                <li>Optional yellow tip: hide under <strong>Settings → Options → Information text</strong>.</li>
            </ul>
        </div>
    </details>

    <details class='topic'>
        <summary class='topic-head'>
            <span class='topic-title'>🔁 Rotations <span class='chevron'>▼</span></span>
            <span class='topic-blurb'>Time Based, Sequential, Position Based, Manual — and the Rotate button</span>
        </summary>
        <div class='topic-body'>
            <ul>
                <li><strong>Settings → Options → Rotation Basis</strong>:
                    <ul>
                        <li><strong>Time Based</strong> (default) — most field time off; least on.</li>
                        <li><strong>Sequential</strong> — roster-order FIFO after the last who rotated.</li>
                        <li><strong>Position Based</strong> — cycles occupied Field cells by row; Bench uses least time.</li>
                        <li><strong>Manual</strong> — you seed pairs (Bench then Field); Rotate runs those pairs, then clears.</li>
                    </ul>
                </li>
                <li><strong>Rotate</strong> (bottom centre): swap queued pairs and reset the countdown (e.g. <span class='key'>Rotate 2</span>).</li>
                <li><strong>Hold Rotate ~1s:</strong> Reset Clk or pick how many pairs to rotate.</li>
                <li>Matching outline colours link field ↔ bench next-up. Vibration warns before a rotation is due.</li>
                <li>On shared teams, only the match controller can rotate.</li>
            </ul>
        </div>
    </details>

    <details class='topic'>
        <summary class='topic-head'>
            <span class='topic-title'>🔄 Rotation View <span class='chevron'>▼</span></span>
            <span class='topic-blurb'>Large glanceable board — who is on / off, scores on the sides</span>
        </summary>
        <div class='topic-body'>
            <p>Built so you can watch the game and glance at the phone for who is coming on and off.</p>
            <ul>
                <li>Opens when you <strong>Start</strong>, or via <span class='key'>View</span> → Rotation.</li>
                <li><strong>Centre:</strong> upcoming swaps — bench on (blue), field off (orange). Tap to rotate (controller only when shared).</li>
                <li><strong>Hold ~1s</strong> for Reset Clk / rotation count.</li>
                <li><strong>Green / red strips:</strong> Us / Them — tap +1, double-tap −1 (controller only).</li>
            </ul>
        </div>
    </details>

    <details class='topic'>
        <summary class='topic-head'>
            <span class='topic-title'>📋 Team View (optional) <span class='chevron'>▼</span></span>
            <span class='topic-blurb'>Legacy roster list — off by default; enable in Options</span>
        </summary>
        <div class='topic-body'>
            <p>Enable under <strong>Settings → Options → Enable Team View</strong>. View then cycles Field → Team → Rotation.</p>
            <ul>
                <li>Row colour:
                    <span class='badge badge-field'>Field</span>
                    <span class='badge badge-bench'>Bench</span>
                    <span class='badge badge-goalie'>Goalie</span>
                    <span class='badge badge-inactive'>Absent</span>
                </li>
                <li>Setup: tap name to rename. Live: same Field↔Bench / Bench↔Absent rules as Field View.</li>
                <li>Swipe to cycle positions; long-press drag on ☰ to reorder (Sequential).</li>
            </ul>
        </div>
    </details>

    <details class='topic'>
        <summary class='topic-head'>
            <span class='topic-title'>🎮 Buttons, scores &amp; Options <span class='chevron'>▼</span></span>
            <span class='topic-blurb'>Start / Pause / View, scoring, Rotation Basis, reminders switches</span>
        </summary>
        <div class='topic-body'>
            <ul>
                <li><strong>Start / Pause</strong> — match timer. Hold ~1s to restart / reset (releases shared match control).</li>
                <li><strong>View</strong> — Field ↔ Rotation by default. Enable Team View in Options to insert the roster list. View-only devices stay on Field.</li>
                <li><strong>Scores</strong> appear when live (header + Rotation strips). Shared: controller only.</li>
                <li><strong>Options:</strong> Rotation Basis, Information text, Enable Team View, Goal scorer &amp; assist, Match reminders.</li>
            </ul>
        </div>
    </details>

    <!-- ── Teams & clubs ── -->
    <details class='topic'>
        <summary class='topic-head'>
            <span class='topic-title'>☁️ Clubs &amp; shared teams <span class='chevron'>▼</span></span>
            <span class='topic-blurb'>Create a club + team, add teams under your club, or use a nickname; join via invite</span>
        </summary>
        <div class='topic-body'>
            <p>Choose <strong>Shared</strong> on Team Details for cloud teams. <strong>Local</strong> is device-only (no Chat / cloud Details).</p>
            <ul>
                <li><strong>New club + team</strong> — enter Club name and Team name. You become <em>Club Owner</em> and Team Owner of the first team. Save the <strong>Club Owner Recovery Code</strong> and <strong>Team Owner Recovery Code</strong> when shown.</li>
                <li><strong>Add team under a club you manage</strong> — pick a managed club (you must be Club Owner or Club Admin), enter a new team name. Club names are <em>not</em> unique worldwide — identity is a hidden club id; invites are how people reach <em>your</em> club.</li>
                <li><strong>Nickname</strong> — standalone shared team with no club.</li>
                <li><strong>Join:</strong> invite code, QR, or <code>turf://v1/join?invite=…</code> link → join as Member. Share from Team Details (QR + link).</li>
                <li>Local team QR import (Import Team) still copies a full offline roster.</li>
                <li>There is <strong>no public club directory</strong> — only people with an invite reach the real club/team.</li>
            </ul>
        </div>
    </details>

    <details class='topic'>
        <summary class='topic-head'>
            <span class='topic-title'>👑 Roles <span class='chevron'>▼</span></span>
            <span class='topic-blurb'>Club Owner / Club Admin · Team Owner / Team Admin / Member</span>
        </summary>
        <div class='topic-body'>
            <p><strong>Club</strong> and <strong>Team</strong> roles are separate.</p>
            <ul>
                <li><strong>Club Owner</strong> — created the club. Elevates Club Admins; creates teams under the club; can recover club ownership with the Club Owner Recovery Code.</li>
                <li><strong>Club Admin</strong> — can create additional teams under that club. Elevated by the Club Owner (often offered when promoting someone to Team Admin).</li>
                <li><strong>Team Owner</strong> — <code>createdBy</code> on that team. Can transfer team ownership, hard-delete the team, remove other Admins. Restored with the <em>Team</em> Owner Recovery Code.</li>
                <li><strong>Team Admin</strong> — run games, edit Location / Kit / Duties, promote Members, manage invite codes (cannot delete the whole team).</li>
                <li><strong>Member</strong> — view-only on Game; Chat and follow roster / timers / scores.</li>
                <li>See <strong>Team Admin Panel → View Team Members</strong> for Owner / Admin / Member on the current team.</li>
            </ul>
        </div>
    </details>

    <details class='topic'>
        <summary class='topic-head'>
            <span class='topic-title'>🔑 Recover Owner Access <span class='chevron'>▼</span></span>
            <span class='topic-blurb'>Same form for Team or Club — which code you enter decides what is restored</span>
        </summary>
        <div class='topic-body'>
            <p>Under Team Details → <strong>Recover Owner Access</strong> (new phone / reinstall):</p>
            <ul>
                <li>Enter a <strong>Team ID</strong> (any team under your club works as an anchor), your recovery code, and display name.</li>
                <li><strong>Team Owner Recovery Code</strong> → restores Team Owner (and Admin) for that team.</li>
                <li><strong>Club Owner Recovery Code</strong> → restores Club Owner. You become Team Admin on the anchor team; team ownership is <em>not</em> stolen.</li>
                <li>Email reminder (same section) can resend the <em>team</em> recovery hint to the email used at create. Club recovery codes are not emailed yet — store them safely when the club is created.</li>
            </ul>
        </div>
    </details>

    <details class='topic'>
        <summary class='topic-head'>
            <span class='topic-title'>🛠️ Team Admin Panel <span class='chevron'>▼</span></span>
            <span class='topic-blurb'>Invite, members, promote, remove, transfer, regenerate code, delete</span>
        </summary>
        <div class='topic-body'>
            <ul>
                <li><strong>Invite Code</strong> — share so others can join this team.</li>
                <li><strong>View Team Members</strong> — (Owner) / (Admin) / (Member).</li>
                <li><strong>Promote to Admin</strong> — elevate a Member to Team Admin. If you are Club Owner, you may also make them a <em>Club Admin</em> so they can create teams under the club.</li>
                <li><strong>Remove Member</strong> — Owner can remove Admins; Admins can remove Members. Use Leave Team to remove yourself.</li>
                <li><strong>Relinquish Match Control</strong> — free the live controller seat.</li>
                <li><strong>Transfer Ownership</strong> (Team Owner only) — pass <em>team</em> ownership to another Admin.</li>
                <li><strong>Regenerate Invite Code</strong> — invalidate the old code.</li>
                <li><strong>Delete team</strong> (swipe under Change Team, Team Owner only) — permanently removes the team from Firebase for everyone.</li>
            </ul>
        </div>
    </details>

    <details class='topic'>
        <summary class='topic-head'>
            <span class='topic-title'>🎮 Single match controller <span class='chevron'>▼</span></span>
            <span class='topic-blurb'>One Admin controls the live match; others are view-only until handover</span>
        </summary>
        <div class='topic-body'>
            <p>Only one Admin at a time may control timers, rotate, scores, and roster edits on a shared team.</p>
            <ul>
                <li><strong>Start</strong> on a free Admin device claims control.</li>
                <li>Yellow banner (Member): view-only. Locked co-Admin: request control; controller Accept / Reject.</li>
                <li>Vacant after Relinquish or ~90s offline: Admins see “Tap to take control.”</li>
                <li><strong>Watch Only</strong> — voluntary view-only without demoting Admin. <strong>Relinquish Match Control</strong> frees the seat without ending the match.</li>
            </ul>
        </div>
    </details>

    <details class='topic'>
        <summary class='topic-head'>
            <span class='topic-title'>👤 Display name <span class='chevron'>▼</span></span>
            <span class='topic-blurb'>Shown in Chat, push, and member lists</span>
        </summary>
        <div class='topic-body'>
            <ul>
                <li>Asked on first launch (or when unset). Required to create or join a shared team.</li>
                <li>Edit under Team Details → Current Team.</li>
            </ul>
        </div>
    </details>

    <details class='topic'>
        <summary class='topic-head'>
            <span class='topic-title'>📍 Details: Location · Kit · Duties <span class='chevron'>▼</span></span>
            <span class='topic-blurb'>Match schedule, kit notes, match-day duties — Admins edit</span>
        </summary>
        <div class='topic-body'>
            <ul>
                <li><strong>Location</strong> — date/time, arrive, venue, maps. Syncs on shared teams; drives match reminders.</li>
                <li><strong>Kit</strong> — arrive / warm-up / game / departure and special events.</li>
                <li><strong>Duties</strong> — duty officer, canteen, grounds, other.</li>
                <li>Admins edit; Members see view-only.</li>
            </ul>
        </div>
    </details>

    <details class='topic'>
        <summary class='topic-head'>
            <span class='topic-title'>⏰ Reminders &amp; chat push <span class='chevron'>▼</span></span>
            <span class='topic-blurb'>Local match reminders vs team chat notifications</span>
        </summary>
        <div class='topic-body'>
            <ul>
                <li><strong>Match reminders</strong> (Settings → Options): day before, morning of, time-to-leave from Details → Location. Device-local — not system Clock alarms.</li>
                <li><strong>Chat push</strong> on shared teams when the app is backgrounded. Separate from match reminders; needs notification permission.</li>
            </ul>
        </div>
    </details>

    <details class='topic'>
        <summary class='topic-head'>
            <span class='topic-title'>💡 Tips <span class='chevron'>▼</span></span>
            <span class='topic-blurb'>Controller device, reinstalls, recovery codes</span>
        </summary>
        <div class='topic-body'>
            <ul>
                <li>Keep the <strong>Game</strong> tab open on the controller’s device when another Admin requests control.</li>
                <li>Reinstall may create a new Firebase identity — use <strong>Recover Owner Access</strong> with your saved Team or Club recovery code.</li>
                <li>Store recovery codes outside the phone (password manager) when you create a club or team.</li>
            </ul>
        </div>
    </details>

</body>
</html>";
    }
}
