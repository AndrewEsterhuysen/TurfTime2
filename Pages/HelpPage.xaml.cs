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

        // Pitch & Volt: re-render the help HTML in the matching light/dark palette when the theme changes.
        if (Application.Current is not null)
            Application.Current.RequestedThemeChanged += (_, _) =>
                MainThread.BeginInvokeOnMainThread(() =>
                    helpWebView.Source = new HtmlWebViewSource { Html = GetHelpHtml() });
    }

    private static string ThemeClass =>
        Application.Current?.RequestedTheme == AppTheme.Dark ? "dark" : "light";

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
        body.light {{ --bg:#F6F8F7; --surface:#FFFFFF; --outline:#D5DDD9; --text:#111816; --text2:#56635E; --primary:#0B6E4F; --pc:#D7F2E3; --onpc:#0B6E4F; --em:#9A4F00; --chip:#EEF2F0; }}
        body.dark  {{ --bg:#0B1210; --surface:#15201C; --outline:#2A3833; --text:#E8EEEB; --text2:#9FB0A9; --primary:#3DDC97; --pc:#0F3B2C; --onpc:#3DDC97; --em:#FFB547; --chip:#1E2B26; }}
        body {{
            font-family: -apple-system, BlinkMacSystemFont, 'Inter', Roboto, Arial, sans-serif;
            background: var(--bg);
            color: var(--text);
            padding: 16px 16px 32px;
            margin: 0;
            font-size: 15px;
            line-height: 1.55;
        }}
        .ico {{ width: 22px; height: 22px; fill: var(--primary); flex-shrink: 0; }}
        h1 {{
            color: var(--text);
            margin: 8px 0 4px;
            font-size: 28px;
            line-height: 1.2;
            display: flex; align-items: center; gap: 8px;
        }}
        h1 .ico {{ width: 32px; height: 32px; }}
        .tagline {{ color: var(--text2); font-size: 15px; margin: 0 0 16px; }}
        .build-box {{
            background: var(--surface);
            border: 1px solid var(--outline);
            padding: 8px 16px;
            border-radius: 16px;
            margin-bottom: 16px;
        }}
        .build-label {{ color: var(--text2); font-size: 11px; font-weight: 600; letter-spacing: .5px; text-transform: uppercase; }}
        .build-value {{ color: var(--text); font-size: 13px; font-family: ui-monospace, Menlo, monospace; word-break: break-all; }}
        .contact {{
            background: var(--pc);
            border-radius: 16px;
            padding: 16px;
            margin-bottom: 16px;
        }}
        .contact-title {{ display: flex; align-items: center; gap: 8px; font-weight: 600; font-size: 17px; }}
        .contact a {{ color: var(--onpc); font-weight: 600; word-break: break-all; }}
        .contact .small {{ margin: 0; color: var(--text2); font-size: 13px; }}
        .hint {{ color: var(--text2); font-size: 13px; margin: 0 0 8px; }}
        details.topic {{
            background: var(--surface);
            border: 1px solid var(--outline);
            border-radius: 16px;
            margin-bottom: 8px;
            overflow: hidden;
        }}
        details.topic[open] {{ border-color: var(--primary); }}
        summary.topic-head {{
            list-style: none;
            cursor: pointer;
            padding: 16px;
            display: flex;
            flex-direction: column;
            gap: 4px;
            -webkit-tap-highlight-color: transparent;
        }}
        summary.topic-head::-webkit-details-marker {{ display: none; }}
        .topic-title {{
            color: var(--text);
            font-weight: 600;
            font-size: 17px;
            display: flex;
            align-items: center;
            gap: 8px;
        }}
        .topic-title .label {{ flex: 1; }}
        .chevron {{ width: 24px; height: 24px; fill: var(--text2); flex-shrink: 0; transition: transform .15s; }}
        details.topic[open] .chevron {{ transform: rotate(180deg); }}
        .topic-blurb {{
            color: var(--text2);
            font-size: 13px;
            font-weight: normal;
            line-height: 1.4;
            padding-left: 30px;
        }}
        .topic-body {{
            padding: 8px 16px 16px;
            border-top: 1px solid var(--outline);
        }}
        .topic-body p {{ margin: 0 0 8px; }}
        ul {{ margin: 8px 0; padding-left: 20px; }}
        li {{ margin-bottom: 8px; }}
        strong {{ color: var(--text); font-weight: 600; }}
        em {{ color: var(--em); font-style: normal; font-weight: 600; }}
        code {{
            background: var(--chip);
            padding: 1px 6px;
            border-radius: 6px;
            font-size: 13px;
        }}
        .badge {{
            display: inline-block;
            padding: 2px 8px;
            border-radius: 999px;
            font-size: 13px;
            font-weight: 600;
            margin: 0 2px;
        }}
        /* Badge colours mirror the Game tab's position colours (unchanged by ui-refresh). */
        .badge-field    {{ background-color: #388e3c; color: #fff; }}
        .badge-bench    {{ background-color: #1565c0; color: #fff; }}
        .badge-goalie   {{ background-color: #f57f17; color: #111816; }}
        .badge-inactive {{ background-color: #424242; color: #fff; }}
        .key {{
            display: inline-block;
            background: var(--chip);
            border: 1px solid var(--outline);
            border-radius: 999px;
            padding: 0 8px;
            font-size: 13px;
            font-weight: 600;
        }}
    </style>
</head>
<body class='{ThemeClass}'>

    <div class='build-box'>
        <div class='build-label'>Current Build</div>
        <div class='build-value'>{GetBuildDateTime()}</div>
    </div>

    <h1><svg class='ico' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M480 80Q397 80 324 112Q251 143 197 197Q143 251 112 324Q80 397 80 480Q80 563 112 636Q143 709 197 763Q251 817 324 848Q397 880 480 880Q563 880 636 848Q709 817 763 763Q817 709 848 636Q880 563 880 480Q880 397 848 324Q817 251 763 197Q709 143 636 112Q563 80 480 80ZM680 580 734 598 750 652Q718 700 673 734Q628 769 574 786L520 748V692ZM280 580 440 692V748L386 786Q332 769 287 734Q242 700 210 652L226 598ZM238 272 284 276 314 330 256 504 200 524 160 494Q160 429 178 376Q196 322 238 272ZM580 176 608 236 582 280H378L352 236L380 176Q404 168 429 164Q454 160 480 160Q506 160 531 164Q556 168 580 176ZM390 360H570L626 520L480 622L336 520ZM722 272Q764 322 782 376Q800 429 800 494L760 522L704 504L646 330L676 276Z'/></svg>Turf Time Help</h1>
    <p class='tagline'>Your sideline companion — Field View by default, fair rotations when you glance at the phone.</p>
    <p class='hint'>Tap a topic below to expand. Only open what you need.</p>

    <div class='contact'>
        <div class='contact-title'><svg class='ico' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M160 160Q127 160 104 184Q80 207 80 240V720Q80 753 104 776Q127 800 160 800H800Q833 800 856 776Q880 753 880 720V240Q880 207 856 184Q833 160 800 160ZM800 640 501 453Q496 450 490 448Q485 447 480 447Q475 447 470 448Q464 450 459 453L160 640V240Q160 240 160 240Q160 240 160 240H800Q800 240 800 240Q800 240 800 240ZM480 520 800 720H160ZM160 640V630Q160 635 160 642Q160 650 160 659Q160 679 160 689Q160 699 160 688V720V688Q160 699 160 688Q160 678 160 659Q160 649 160 642Q160 634 160 630V640V240Q160 240 160 240Q160 240 160 240Q160 240 160 240Q160 240 160 240Z'/></svg>Feature requests &amp; bugs</div>
        <p style='margin:8px 0 4px;'>Email
            <a href='mailto:andrew.esterhuysen00@gmail.com'>andrew.esterhuysen00@gmail.com</a>
        </p>
        <p class='small'>Include device type, the build box above, and steps to reproduce.</p>
    </div>

    <!-- ── Game play ── -->
    <details class='topic'>
        <summary class='topic-head'>
            <span class='topic-title'><svg class='ico' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M480 80Q397 80 324 112Q251 143 197 197Q143 251 112 324Q80 397 80 480Q80 563 112 636Q143 709 197 763Q251 817 324 848Q397 880 480 880Q563 880 636 848Q709 817 763 763Q817 709 848 636Q880 563 880 480Q880 397 848 324Q817 251 763 197Q709 143 636 112Q563 80 480 80ZM680 580 734 598 750 652Q718 700 673 734Q628 769 574 786L520 748V692ZM280 580 440 692V748L386 786Q332 769 287 734Q242 700 210 652L226 598ZM238 272 284 276 314 330 256 504 200 524 160 494Q160 429 178 376Q196 322 238 272ZM580 176 608 236 582 280H378L352 236L380 176Q404 168 429 164Q454 160 480 160Q506 160 531 164Q556 168 580 176ZM390 360H570L626 520L480 622L336 520ZM722 272Q764 322 782 376Q800 429 800 494L760 522L704 504L646 330L676 276Z'/></svg><span class='label'>Field View</span><svg class='chevron' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M452 373 268 557Q257 568 257 585Q257 602 268 613Q279 624 296 624Q313 624 324 613L480 457L636 613Q647 624 664 624Q681 624 692 613Q703 602 703 585Q703 568 692 557L508 373Q502 367 495 364Q488 362 480 362Q472 362 465 364Q458 367 452 373Z'/></svg></span>
            <span class='topic-blurb'>Default Game screen — pitch, Bench, Goalie, Absent, live subs</span>
        </summary>
        <div class='topic-body'>
            <p>Main screen for setup and the match. Watch the pitch; use the phone when placing or rotating.</p>
            <ul>
                <li><strong>4×4 pitch</strong> (cells 1–16): one outfield player per cell. Drag or tap-to-place from Bench / Goalie / Absent.</li>
                <li><strong>Bench</strong> (right): new players start here. Drag or tap onto pitch, Goalie, or Absent. Stays visible during the match.</li>
                <li><strong>Goalie</strong> (over the goal): assign the keeper. A <strong>+</strong> appears on valid drop targets while dragging.</li>
                <li><strong>Absent</strong> (behind goalie): unavailable players. Move-only; stays visible after Start for late arrivals.</li>
                <li><strong>Setup:</strong> tap a token to arm it, then tap a destination. Double-tap to rename — prefer <em>first + surname</em> (e.g. Andrew Esterhuysen).</li>
                <li><strong>Live:</strong> Field→Bench = sub out; Bench→Field seeds/queues next-up (depends on Rotation Basis); Bench→Absent = injury; Absent→Bench = late arrival. Live Field→Absent is blocked.</li>
                <li>Tokens show a short label + discreet <strong>MM:SS</strong> (Field / Goalie / Bench). Full names become initials like <strong>A.E</strong>; single words use the first three letters; defaults stay <strong>#01</strong>. Outlines mark next-up / pairs.</li>
                <li>Optional yellow tip: hide under <strong>Settings → Options → Information text</strong>.</li>
            </ul>
        </div>
    </details>

    <details class='topic'>
        <summary class='topic-head'>
            <span class='topic-title'><svg class='ico' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M240 478Q240 462 242 446Q244 431 249 416Q254 399 248 384Q242 368 227 361Q211 353 196 360Q180 366 175 383Q167 406 164 430Q160 454 160 478Q160 612 253 706Q346 800 480 800H487L451 836Q440 847 440 864Q440 881 451 892Q462 903 479 903Q496 903 507 892L611 788Q623 776 623 760Q623 744 611 732L507 628Q496 617 479 617Q462 617 451 628Q440 639 440 656Q440 673 451 684L487 720H480Q380 720 310 650Q240 579 240 478ZM720 482Q720 498 718 514Q716 529 711 544Q706 561 712 576Q718 592 733 599Q749 607 764 600Q780 594 785 577Q793 554 796 530Q800 506 800 482Q800 348 707 254Q614 160 480 160H473L509 124Q520 113 520 96Q520 79 509 68Q498 57 481 57Q464 57 453 68L349 172Q337 184 337 200Q337 216 349 228L453 332Q464 343 481 343Q498 343 509 332Q520 321 520 304Q520 287 509 276L473 240H480Q580 240 650 310Q720 381 720 482Z'/></svg><span class='label'>Rotations</span><svg class='chevron' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M452 373 268 557Q257 568 257 585Q257 602 268 613Q279 624 296 624Q313 624 324 613L480 457L636 613Q647 624 664 624Q681 624 692 613Q703 602 703 585Q703 568 692 557L508 373Q502 367 495 364Q488 362 480 362Q472 362 465 364Q458 367 452 373Z'/></svg></span>
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
            <span class='topic-title'><svg class='ico' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M233 280 309 204Q321 192 320 176Q320 160 308 148Q296 137 280 136Q264 136 252 148L108 292Q102 298 100 305Q97 312 97 320Q97 328 100 335Q102 342 108 348L252 492Q263 503 280 503Q296 503 308 492Q320 480 320 464Q320 447 308 435L233 360H800Q817 360 828 348Q840 337 840 320Q840 303 828 292Q817 280 800 280ZM727 600H160Q143 600 132 612Q120 623 120 640Q120 657 132 668Q143 680 160 680H727L651 756Q639 768 640 784Q640 800 652 812Q664 823 680 824Q696 824 708 812L852 668Q858 662 860 655Q863 648 863 640Q863 632 860 625Q858 618 852 612L708 468Q697 457 680 457Q664 457 652 468Q640 480 640 496Q640 513 652 525Z'/></svg><span class='label'>Rotation View</span><svg class='chevron' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M452 373 268 557Q257 568 257 585Q257 602 268 613Q279 624 296 624Q313 624 324 613L480 457L636 613Q647 624 664 624Q681 624 692 613Q703 602 703 585Q703 568 692 557L508 373Q502 367 495 364Q488 362 480 362Q472 362 465 364Q458 367 452 373Z'/></svg></span>
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
            <span class='topic-title'><svg class='ico' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M360 240H800Q800 240 800 240Q800 240 800 240V347Q800 347 800 347Q800 347 800 347H360Q360 347 360 347Q360 347 360 347V240Q360 240 360 240Q360 240 360 240ZM160 613H280Q280 613 280 613Q280 613 280 613V720Q280 720 280 720Q280 720 280 720H160Q160 720 160 720Q160 720 160 720V613Q160 613 160 613Q160 613 160 613ZM160 426H280Q280 426 280 426Q280 426 280 426V533Q280 533 280 533Q280 533 280 533H160Q160 533 160 533Q160 533 160 533V426Q160 426 160 426Q160 426 160 426ZM160 240H280Q280 240 280 240Q280 240 280 240V347Q280 347 280 347Q280 347 280 347H160Q160 347 160 347Q160 347 160 347V240Q160 240 160 240Q160 240 160 240ZM360 426H800Q800 426 800 426Q800 426 800 426V533Q800 533 800 533Q800 533 800 533H360Q360 533 360 533Q360 533 360 533V426Q360 426 360 426Q360 426 360 426ZM360 613H800Q800 613 800 613Q800 613 800 613V720Q800 720 800 720Q800 720 800 720H360Q360 720 360 720Q360 720 360 720V613Q360 613 360 613Q360 613 360 613ZM160 160Q127 160 104 184Q80 207 80 240V720Q80 753 104 776Q127 800 160 800H800Q833 800 856 776Q880 753 880 720V240Q880 207 856 184Q833 160 800 160Z'/></svg><span class='label'>Team View (optional)</span><svg class='chevron' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M452 373 268 557Q257 568 257 585Q257 602 268 613Q279 624 296 624Q313 624 324 613L480 457L636 613Q647 624 664 624Q681 624 692 613Q703 602 703 585Q703 568 692 557L508 373Q502 367 495 364Q488 362 480 362Q472 362 465 364Q458 367 452 373Z'/></svg></span>
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
                <li>Setup: tap name to rename (prefer first + surname so Field tokens show A.E-style initials). Live: same Field↔Bench / Bench↔Absent rules as Field View.</li>
                <li>Swipe to cycle positions; long-press drag on ☰ to reorder (Sequential).</li>
            </ul>
        </div>
    </details>

    <details class='topic'>
        <summary class='topic-head'>
            <span class='topic-title'><svg class='ico' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M182 200Q131 200 103 236Q75 271 82 322L124 622Q133 682 178 721Q222 760 282 760H678Q738 760 782 721Q827 682 836 622L878 322Q885 271 857 236Q829 200 778 200Q757 200 739 208Q721 215 706 230L616 320H344L254 230Q239 215 221 208Q203 200 182 200ZM198 286 312 400H648L762 286Q764 284 778 280Q789 280 796 286Q802 293 800 304L756 612Q752 641 730 660Q708 680 678 680H282Q252 680 230 660Q208 641 204 612L160 304Q158 293 164 286Q171 280 182 280Q184 280 198 286ZM680 440Q697 440 708 452Q720 463 720 480Q720 497 708 508Q697 520 680 520Q663 520 652 508Q640 497 640 480Q640 463 652 452Q663 440 680 440ZM600 560Q617 560 628 572Q640 583 640 600Q640 617 628 628Q617 640 600 640Q583 640 572 628Q560 617 560 600Q560 583 572 572Q583 560 600 560ZM480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480ZM310 510V470Q310 457 318 448Q327 440 340 440Q353 440 362 448Q370 457 370 470V510H410Q423 510 432 518Q440 527 440 540Q440 553 432 562Q423 570 410 570H370V610Q370 623 362 632Q353 640 340 640Q327 640 318 632Q310 623 310 610V570H270Q257 570 248 562Q240 553 240 540Q240 527 248 518Q257 510 270 510Z'/></svg><span class='label'>Buttons, scores &amp; Options</span><svg class='chevron' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M452 373 268 557Q257 568 257 585Q257 602 268 613Q279 624 296 624Q313 624 324 613L480 457L636 613Q647 624 664 624Q681 624 692 613Q703 602 703 585Q703 568 692 557L508 373Q502 367 495 364Q488 362 480 362Q472 362 465 364Q458 367 452 373Z'/></svg></span>
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
            <span class='topic-title'><svg class='ico' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M260 160Q169 160 104 223Q40 286 40 377Q40 455 87 516Q134 577 210 594Q235 686 310 743Q385 800 480 800Q597 800 678 718Q760 637 760 520Q829 512 874 460Q920 409 920 340Q920 265 868 212Q815 160 740 160ZM260 240H740Q782 240 811 269Q840 298 840 340Q840 382 811 411Q782 440 740 440H680V520Q680 603 622 662Q563 720 480 720Q397 720 338 662Q280 603 280 520H260Q202 520 161 479Q120 438 120 380Q120 322 161 281Q202 240 260 240ZM480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Z'/></svg><span class='label'>Clubs &amp; shared teams</span><svg class='chevron' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M452 373 268 557Q257 568 257 585Q257 602 268 613Q279 624 296 624Q313 624 324 613L480 457L636 613Q647 624 664 624Q681 624 692 613Q703 602 703 585Q703 568 692 557L508 373Q502 367 495 364Q488 362 480 362Q472 362 465 364Q458 367 452 373Z'/></svg></span>
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
            <span class='topic-title'><svg class='ico' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M480 483 412 431Q406 426 400 430Q394 435 396 442L422 526L352 582Q347 587 349 594Q351 600 358 600H444L470 682Q472 689 480 689Q488 689 490 682L516 600H601Q608 600 610 594Q613 587 608 582L537 526L563 442Q565 435 559 430Q553 426 547 431ZM480 120 293 58Q273 51 256 63Q240 75 240 95V349Q202 391 181 445Q160 499 160 560Q160 694 253 787Q346 880 480 880Q614 880 707 787Q800 694 800 560Q800 499 779 445Q758 391 720 349V95Q720 75 704 63Q687 51 667 58ZM480 320Q580 320 650 390Q720 460 720 560Q720 660 650 730Q580 800 480 800Q380 800 310 730Q240 660 240 560Q240 460 310 390Q380 320 480 320ZM320 159 480 200 640 159V283Q605 263 564 252Q524 240 480 240Q436 240 396 252Q355 263 320 283ZM480 221Q480 221 480 221Q480 221 480 221Q480 221 480 221Q480 221 480 221Z'/></svg><span class='label'>Roles</span><svg class='chevron' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M452 373 268 557Q257 568 257 585Q257 602 268 613Q279 624 296 624Q313 624 324 613L480 457L636 613Q647 624 664 624Q681 624 692 613Q703 602 703 585Q703 568 692 557L508 373Q502 367 495 364Q488 362 480 362Q472 362 465 364Q458 367 452 373Z'/></svg></span>
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
                <li>See <strong>Team Admin Panel → View Team Members</strong> (all members) for Owner / Admin / Member on the current team.</li>
            </ul>
        </div>
    </details>

    <details class='topic'>
        <summary class='topic-head'>
            <span class='topic-title'><svg class='ico' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M280 400Q247 400 224 424Q200 447 200 480Q200 513 224 536Q247 560 280 560Q313 560 336 536Q360 513 360 480Q360 447 336 424Q313 400 280 400ZM280 240Q180 240 110 310Q40 380 40 480Q40 580 110 650Q180 720 280 720Q347 720 402 687Q456 654 488 600H823Q831 600 838 597Q846 594 852 588L932 508Q938 502 940 495Q943 488 943 480Q943 472 940 465Q938 458 932 452L805 325Q800 320 793 317Q786 314 779 313Q772 312 765 314Q758 316 752 321L700 360L643 317Q638 313 632 311Q626 309 620 309Q614 309 608 311Q601 313 596 317L535 360H488Q456 306 402 273Q347 240 280 240ZM280 320Q336 320 378 354Q421 388 435 440H560L618 399Q618 399 618 398Q618 398 618 399Q618 399 618 399Q618 399 618 399L700 460L771 405Q771 405 771 405Q771 405 771 405Q771 405 771 405Q771 405 771 405L846 480Q845 480 846 480Q846 480 846 480Q846 480 846 480Q846 480 846 480L806 520Q806 521 806 520Q806 520 806 520H435Q421 572 378 606Q336 640 280 640Q214 640 167 593Q120 546 120 480Q120 414 167 367Q214 320 280 320Z'/></svg><span class='label'>Recover Owner Access</span><svg class='chevron' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M452 373 268 557Q257 568 257 585Q257 602 268 613Q279 624 296 624Q313 624 324 613L480 457L636 613Q647 624 664 624Q681 624 692 613Q703 602 703 585Q703 568 692 557L508 373Q502 367 495 364Q488 362 480 362Q472 362 465 364Q458 367 452 373Z'/></svg></span>
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
            <span class='topic-title'><svg class='ico' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M480 874Q488 874 494 873Q501 872 508 869L748 779Q771 770 786 750Q800 730 800 705V580Q800 563 788 552Q777 540 760 540Q743 540 732 552Q720 563 720 580V704Q720 704 720 704Q720 704 720 704L480 794Q480 794 480 794Q480 794 480 794L240 704Q240 704 240 704Q240 704 240 704V516Q240 466 254 416Q269 366 294 321Q320 276 356 240Q393 204 436 181Q451 173 458 158Q464 143 458 128Q451 112 436 106Q420 100 405 108Q292 164 226 277Q160 390 160 516V705Q160 730 174 750Q189 770 212 779L452 869Q459 872 466 873Q473 874 480 874ZM680 80Q597 80 538 138Q480 197 480 280Q480 363 538 422Q597 480 680 480Q763 480 822 422Q880 363 880 280Q880 197 822 138Q763 80 680 80ZM480 494Q480 494 480 494Q480 494 480 494Q480 494 480 494Q480 494 480 494Q480 494 480 494Q480 494 480 494Q480 494 480 494Q480 494 480 494Q480 494 480 494Q480 494 480 494Q480 494 480 494Q480 494 480 494Q480 494 480 494Q480 494 480 494Q480 494 480 494Q480 494 480 494ZM740 340Q740 365 722 382Q705 400 680 400Q655 400 638 382Q620 365 620 340Q620 315 638 298Q655 280 680 280Q705 280 722 298Q740 315 740 340ZM680 160Q705 160 727 170Q749 179 766 197Q771 203 770 210Q769 218 761 222Q742 231 722 236Q701 240 680 240Q659 240 638 236Q618 231 599 222Q591 218 590 210Q589 203 594 197Q611 179 633 170Q655 160 680 160Z'/></svg><span class='label'>Team Admin Panel</span><svg class='chevron' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M452 373 268 557Q257 568 257 585Q257 602 268 613Q279 624 296 624Q313 624 324 613L480 457L636 613Q647 624 664 624Q681 624 692 613Q703 602 703 585Q703 568 692 557L508 373Q502 367 495 364Q488 362 480 362Q472 362 465 364Q458 367 452 373Z'/></svg></span>
            <span class='topic-blurb'>Invite, members, promote, remove, transfer, regenerate code, delete</span>
        </summary>
        <div class='topic-body'>
            <ul>
                <li><strong>View Team Members</strong> — available to <em>all</em> online team members. Shows (Owner) / (Admin) / (Member) so everyone knows who to ask about admin matters.</li>
                <li><strong>Invite Code</strong> — Admin only; share so others can join this team.</li>
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
            <span class='topic-title'><svg class='ico' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M182 200Q131 200 103 236Q75 271 82 322L124 622Q133 682 178 721Q222 760 282 760H678Q738 760 782 721Q827 682 836 622L878 322Q885 271 857 236Q829 200 778 200Q757 200 739 208Q721 215 706 230L616 320H344L254 230Q239 215 221 208Q203 200 182 200ZM198 286 312 400H648L762 286Q764 284 778 280Q789 280 796 286Q802 293 800 304L756 612Q752 641 730 660Q708 680 678 680H282Q252 680 230 660Q208 641 204 612L160 304Q158 293 164 286Q171 280 182 280Q184 280 198 286ZM680 440Q697 440 708 452Q720 463 720 480Q720 497 708 508Q697 520 680 520Q663 520 652 508Q640 497 640 480Q640 463 652 452Q663 440 680 440ZM600 560Q617 560 628 572Q640 583 640 600Q640 617 628 628Q617 640 600 640Q583 640 572 628Q560 617 560 600Q560 583 572 572Q583 560 600 560ZM480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480Q480 480 480 480ZM310 510V470Q310 457 318 448Q327 440 340 440Q353 440 362 448Q370 457 370 470V510H410Q423 510 432 518Q440 527 440 540Q440 553 432 562Q423 570 410 570H370V610Q370 623 362 632Q353 640 340 640Q327 640 318 632Q310 623 310 610V570H270Q257 570 248 562Q240 553 240 540Q240 527 248 518Q257 510 270 510Z'/></svg><span class='label'>Single match controller</span><svg class='chevron' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M452 373 268 557Q257 568 257 585Q257 602 268 613Q279 624 296 624Q313 624 324 613L480 457L636 613Q647 624 664 624Q681 624 692 613Q703 602 703 585Q703 568 692 557L508 373Q502 367 495 364Q488 362 480 362Q472 362 465 364Q458 367 452 373Z'/></svg></span>
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
            <span class='topic-title'><svg class='ico' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M480 480Q414 480 367 527Q320 574 320 640Q320 706 367 753Q414 800 480 800Q546 800 593 753Q640 706 640 640Q640 574 593 527Q546 480 480 480ZM160 240V272Q160 306 178 334Q195 363 224 378Q286 409 350 424Q414 440 480 440Q546 440 610 424Q674 409 736 378Q765 363 782 334Q800 306 800 272V240Q800 207 776 184Q753 160 720 160H240Q207 160 184 184Q160 207 160 240ZM240 240H720V272Q720 283 714 292Q709 301 700 306Q646 333 591 346Q536 360 480 360Q424 360 369 346Q314 333 260 306Q251 301 246 292Q240 283 240 272ZM480 560Q513 560 536 584Q560 607 560 640Q560 673 536 696Q513 720 480 720Q447 720 424 696Q400 673 400 640Q400 607 424 584Q447 560 480 560ZM480 640Q480 640 480 640Q480 640 480 640Q480 640 480 640Q480 640 480 640Q480 640 480 640Q480 640 480 640Q480 640 480 640Q480 640 480 640ZM480 240Q480 240 480 240Q480 240 480 240Q480 240 480 240Q480 240 480 240Q480 240 480 240Q480 240 480 240Q480 240 480 240Q480 240 480 240Z'/></svg><span class='label'>Display name</span><svg class='chevron' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M452 373 268 557Q257 568 257 585Q257 602 268 613Q279 624 296 624Q313 624 324 613L480 457L636 613Q647 624 664 624Q681 624 692 613Q703 602 703 585Q703 568 692 557L508 373Q502 367 495 364Q488 362 480 362Q472 362 465 364Q458 367 452 373Z'/></svg></span>
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
            <span class='topic-title'><svg class='ico' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M480 186Q602 298 661 390Q720 481 720 552Q720 661 650 730Q581 800 480 800Q379 800 310 730Q240 661 240 552Q240 481 299 390Q358 298 480 186ZM427 127Q362 187 312 244Q262 301 228 354Q195 408 178 458Q160 507 160 552Q160 702 256 791Q353 880 480 880Q607 880 704 791Q800 702 800 552Q800 507 782 458Q765 408 732 354Q698 301 648 244Q598 187 533 127Q522 117 508 112Q494 107 480 107Q466 107 452 112Q438 117 427 127ZM480 560Q480 560 480 560Q480 560 480 560Q480 560 480 560Q480 560 480 560Q480 560 480 560Q480 560 480 560Q480 560 480 560Q480 560 480 560ZM480 480Q513 480 536 504Q560 527 560 560Q560 593 536 616Q513 640 480 640Q447 640 424 616Q400 593 400 560Q400 527 424 504Q447 480 480 480Z'/></svg><span class='label'>Details: Location · Kit · Duties</span><svg class='chevron' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M452 373 268 557Q257 568 257 585Q257 602 268 613Q279 624 296 624Q313 624 324 613L480 457L636 613Q647 624 664 624Q681 624 692 613Q703 602 703 585Q703 568 692 557L508 373Q502 367 495 364Q488 362 480 362Q472 362 465 364Q458 367 452 373Z'/></svg></span>
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
            <span class='topic-title'><svg class='ico' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M200 200Q183 200 172 212Q160 223 160 240Q160 257 172 268Q183 280 200 280H240V560Q240 643 290 708Q340 772 420 792V820Q420 845 438 862Q455 880 480 880Q505 880 522 862Q540 845 540 820V792Q620 772 670 708Q720 643 720 560V280H760Q777 280 788 268Q800 257 800 240Q800 223 788 212Q777 200 760 200ZM480 500Q480 500 480 500Q480 500 480 500Q480 500 480 500Q480 500 480 500ZM480 80Q447 80 424 104Q400 127 400 160H560Q560 127 536 104Q513 80 480 80ZM320 280H640V560Q640 626 593 673Q546 720 480 720Q414 720 367 673Q320 626 320 560Z'/></svg><span class='label'>Reminders &amp; chat push</span><svg class='chevron' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M452 373 268 557Q257 568 257 585Q257 602 268 613Q279 624 296 624Q313 624 324 613L480 457L636 613Q647 624 664 624Q681 624 692 613Q703 602 703 585Q703 568 692 557L508 373Q502 367 495 364Q488 362 480 362Q472 362 465 364Q458 367 452 373Z'/></svg></span>
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
            <span class='topic-title'><svg class='ico' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M400 160H560Q560 127 536 104Q513 80 480 80Q447 80 424 104Q400 127 400 160ZM360 200Q343 200 332 212Q320 223 320 240Q320 257 332 268Q343 280 360 280H600Q617 280 628 268Q640 257 640 240Q640 223 628 212Q617 200 600 200ZM330 320Q261 361 220 430Q180 499 180 580Q180 705 268 792Q355 880 480 880Q605 880 692 792Q780 705 780 580Q780 499 740 430Q699 361 630 320ZM354 400H606Q651 432 676 479Q700 526 700 580Q700 672 636 736Q572 800 480 800Q388 800 324 736Q260 672 260 580Q260 526 284 479Q309 432 354 400ZM480 400Q480 400 480 400Q480 400 480 400Q480 400 480 400Q480 400 480 400Q480 400 480 400Q480 400 480 400Q480 400 480 400Q480 400 480 400Z'/></svg><span class='label'>Tips</span><svg class='chevron' viewBox='0 0 960 960' aria-hidden='true'><path transform='matrix(1 0 0 -1 0 960)' d='M452 373 268 557Q257 568 257 585Q257 602 268 613Q279 624 296 624Q313 624 324 613L480 457L636 613Q647 624 664 624Q681 624 692 613Q703 602 703 585Q703 568 692 557L508 373Q502 367 495 364Q488 362 480 362Q472 362 465 364Q458 367 452 373Z'/></svg></span>
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
