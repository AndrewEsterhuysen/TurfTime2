#!/usr/bin/env python3
"""One-off backfill for the Club Admin role (to-do item 8): set
teams/{id}/metadata/info.teamAdminCount = number of teams/{id}/members with
role 'admin'.

Rules treat a missing teamAdminCount as "handed over" (club admins read only),
so without this backfill every existing club team stays read-only to club
admins, even one with no team Admin. After the syncTeamAdminCount Cloud
Function is deployed it keeps the count up to date; run this once after that.

It also stamps members/{uid}.seenAtUtc = now on every team Admin that has none, so the
club owner's "remove inactive team Admin" clock (1 day) starts from the deploy, not from
nothing. (The Function stamps new Admins from then on; the app refreshes the stamp.)

Default is a dry run (no writes). Do NOT run --apply against production until
Andrew has approved the item 8 rules + function deploy.

Usage (from a machine with gcloud ADC, e.g. the Mac):
  export PATH="/opt/homebrew/bin:/opt/homebrew/share/google-cloud-sdk/bin:$PATH"
  python3 scripts/backfill_team_admin_count.py --project turf-timer
  python3 scripts/backfill_team_admin_count.py --project turf-timer --apply
"""
from __future__ import annotations

import argparse, sys, urllib.parse
from datetime import datetime, timezone
from collections import Counter

sys.path.insert(0, __import__("os").path.dirname(__file__))
from migrate_membership_secrets import BASE, PROJECT_DEFAULT, token, req, list_cg, fields_of, path_parts, str_val  # noqa: E402


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--project", default=PROJECT_DEFAULT)
    ap.add_argument("--apply", action="store_true", help="Write teamAdminCount (default is dry-run)")
    args = ap.parse_args()
    tok = token()

    admins: Counter[str] = Counter()
    unstamped: list[tuple[str, str, str]] = []  # (team, uid, full doc name)
    for doc in list_cg(tok, args.project, "members"):
        parts = path_parts(doc.get("name", ""))
        if len(parts) == 4 and parts[0] == "teams" and str_val(fields_of(doc), "role") == "admin":
            admins[parts[1]] += 1
            if "seenAtUtc" not in fields_of(doc):
                unstamped.append((parts[1], parts[3], doc.get("name", "")))

    teams = ok = changed = 0
    for doc in list_cg(tok, args.project, "metadata"):
        parts = path_parts(doc.get("name", ""))
        if not (len(parts) == 4 and parts[0] == "teams" and parts[3] == "info"):
            continue
        teams += 1
        tid = parts[1]
        f = fields_of(doc)
        want = admins.get(tid, 0)
        have = f.get("teamAdminCount", {}).get("integerValue")
        club = str_val(f, "clubId")
        label = f"team {tid}{' (club ' + club + ')' if club else ''}"
        if have is not None and int(have) == want:
            ok += 1
            continue
        changed += 1
        state = "club-managed" if want == 0 else "handed over"
        print(f"  {label}: teamAdminCount {have if have is not None else 'missing'} -> {want}"
              + (f"  [{state}]" if club else ""))
        if args.apply:
            path = f"projects/{args.project}/databases/(default)/documents/teams/{tid}/metadata/info"
            url = f"{BASE}/{path}?updateMask.fieldPaths=teamAdminCount&currentDocument.exists=true"
            req("PATCH", url, tok, {"fields": {"teamAdminCount": {"integerValue": str(want)}}}, args.project)
    now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    for tid, uid, name in unstamped:
        print(f"  team {tid} admin {uid}: seenAtUtc missing -> {now}")
        if args.apply:
            url = f"https://firestore.googleapis.com/v1/{name}?updateMask.fieldPaths=seenAtUtc&currentDocument.exists=true"
            req("PATCH", url, tok, {"fields": {"seenAtUtc": {"timestampValue": now}}}, args.project)
    print(f"admins_to_stamp={len(unstamped)}")
    print(f"teams={teams} already_ok={ok} {'updated' if args.apply else 'would_update'}={changed}"
          + ("" if args.apply else " (dry run)"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
