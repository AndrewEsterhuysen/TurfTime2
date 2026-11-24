#!/usr/bin/env python3
"""One-off migration: copy inviteCode, adminCodeHash, creatorEmail (and club
ownerRecoveryCodeHash) from world-readable metadata into
teams/{id}/secrets/info and clubs/{id}/secrets/info.

Default is a dry run (no writes). Do NOT run --apply against production until
Andrew has approved a deploy window, build 74 is out, and a backup is taken.

Usage (from a machine with gcloud ADC, e.g. the Mac):
  export PATH="/opt/homebrew/bin:/opt/homebrew/share/google-cloud-sdk/bin:$PATH"
  python3 scripts/migrate_membership_secrets.py --project turf-timer
  python3 scripts/migrate_membership_secrets.py --project turf-timer --apply
  python3 scripts/migrate_membership_secrets.py --project turf-timer --apply --strip

--strip also deletes the secret fields from metadata (closes world-readable
leak). Only do this after old app builds no longer need those metadata fields.
"""
from __future__ import annotations

import argparse, json, subprocess, sys, urllib.parse, urllib.request

PROJECT_DEFAULT = "turf-timer"
TEAM_FIELDS = ("inviteCode", "adminCodeHash", "creatorEmail")
CLUB_FIELDS = ("ownerRecoveryCodeHash",)
BASE = "https://firestore.googleapis.com/v1"

def token() -> str:
    out = subprocess.check_output(
        ["gcloud", "auth", "application-default", "print-access-token"], text=True
    )
    return out.strip()

def req(method: str, url: str, tok: str, body: dict | None = None, project: str = PROJECT_DEFAULT):
    data = None if body is None else json.dumps(body).encode()
    r = urllib.request.Request(url, data=data, method=method)
    r.add_header("Authorization", f"Bearer {tok}")
    r.add_header("x-goog-user-project", project)
    if body is not None:
        r.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(r) as resp:
        return json.loads(resp.read().decode())

def list_cg(tok: str, project: str, collection_id: str):
    """Page through a collection-group runQuery."""
    parent = f"{BASE}/projects/{project}/databases/(default)/documents"
    body = {
        "structuredQuery": {
            "from": [{"collectionId": collection_id, "allDescendants": True}],
        }
    }
    # runQuery is a stream of results; use the :runQuery endpoint
    url = f"{parent}:runQuery"
    results = req("POST", url, tok, body, project)
    docs = []
    for item in results:
        doc = item.get("document")
        if doc:
            docs.append(doc)
    return docs

def fields_of(doc: dict) -> dict:
    return doc.get("fields") or {}

def str_val(fields: dict, key: str) -> str:
    v = fields.get(key, {})
    return (v.get("stringValue") or "").strip()

def to_firestore(vals: dict[str, str]) -> dict:
    return {k: {"stringValue": v} for k, v in vals.items() if v}

def path_parts(name: str) -> list[str]:
    # projects/p/databases/(default)/documents/teams/{id}/metadata/info
    marker = "/documents/"
    i = name.find(marker)
    return name[i + len(marker):].split("/") if i >= 0 else []

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--project", default=PROJECT_DEFAULT)
    ap.add_argument("--apply", action="store_true", help="Write secrets (default is dry-run)")
    ap.add_argument("--strip", action="store_true", help="With --apply, also remove secret fields from metadata")
    args = ap.parse_args()
    if args.strip and not args.apply:
        print("--strip requires --apply", file=sys.stderr)
        return 2

    tok = token()
    mode = "APPLY" + ("+STRIP" if args.strip else "") if args.apply else "DRY-RUN"
    print(f"[{mode}] project={args.project}")

    meta_docs = list_cg(tok, args.project, "metadata")
    teams = 0
    clubs = 0
    team_writes = 0
    club_writes = 0
    strips = 0

    for doc in meta_docs:
        parts = path_parts(doc.get("name", ""))
        if len(parts) != 4 or parts[1] not in ("teams", "clubs") and parts[0] not in ("teams", "clubs"):
            # expect teams/{id}/metadata/info or clubs/{id}/metadata/info
            pass
        if len(parts) == 4 and parts[0] in ("teams", "clubs") and parts[2] == "metadata" and parts[3] == "info":
            kind, ident = parts[0], parts[1]
        else:
            continue

        fields = fields_of(doc)
        if kind == "teams":
            teams += 1
            payload = {k: str_val(fields, k) for k in TEAM_FIELDS}
            if not any(payload.values()):
                continue
            secret_path = f"projects/{args.project}/databases/(default)/documents/teams/{ident}/secrets/info"
            print(f"  team {ident}: { {k: ('set' if v else '') for k,v in payload.items()} }")
            if args.apply:
                url = f"{BASE}/{secret_path}?currentDocument.exists=false"
                # Merge always (doc may already exist)
                mask = "&".join(f"updateMask.fieldPaths={urllib.parse.quote(k)}" for k, v in payload.items() if v)
                url = f"{BASE}/{secret_path}?{mask}"
                req("PATCH", url, tok, {"fields": to_firestore(payload)}, args.project)
                team_writes += 1
                if args.strip:
                    dels = {k: None for k in TEAM_FIELDS if str_val(fields, k)}
                    # Firestore REST delete via updateMask and empty fields? Use update with FieldTransform isn't trivial.
                    # PATCH with updateMask and omit fields does not delete. Need {:} skip.
                    # Use commit with FieldTransform? Simpler: document in comments that strip uses a second tool.
                    # Implement via commits: update + field deletes.
                    strip_team_metadata(tok, args.project, ident, [k for k in TEAM_FIELDS if str_val(fields, k)])
                    strips += 1
        else:
            clubs += 1
            payload = {k: str_val(fields, k) for k in CLUB_FIELDS}
            if not any(payload.values()):
                continue
            print(f"  club {ident}: ownerRecoveryCodeHash={'set' if payload.get('ownerRecoveryCodeHash') else ''}")
            if args.apply:
                secret_path = f"projects/{args.project}/databases/(default)/documents/clubs/{ident}/secrets/info"
                mask = "&".join(f"updateMask.fieldPaths={urllib.parse.quote(k)}" for k, v in payload.items() if v)
                url = f"{BASE}/{secret_path}?{mask}"
                req("PATCH", url, tok, {"fields": to_firestore(payload)}, args.project)
                club_writes += 1
                if args.strip:
                    strip_club_metadata(tok, args.project, ident, [k for k in CLUB_FIELDS if str_val(fields, k)])
                    strips += 1

    print(f"Scanned teams={teams} clubs={clubs}. Writes teams={team_writes} clubs={club_writes} strips={strips}.")
    if not args.apply:
        print("Dry run only. Re-run with --apply to write secrets.")
    return 0

def strip_fields(tok, project, path, keys):
    # Write a commit with field deletes via updateMask and no values: Firestore REST
    # uses writes[].updateMask + currentDocument and empty fields for delete? 
    # The reliable way: writes with updateTransforms aren't for delete.
    # Use: PATCH with a document that sets fields to null via `nullValue`? That stores null.
    # Official: in v1, omit from document and include in updateMask deletes the field? 
    # Actually YES: if a field is in updateMask but not in the document fields, it is deleted.
    url = f"{BASE}/{path}?" + "&".join(f"updateMask.fieldPaths={urllib.parse.quote(k)}" for k in keys)
    req("PATCH", url, tok, {"fields": {}}, project)

def strip_team_metadata(tok, project, team_id, keys):
    path = f"projects/{project}/databases/(default)/documents/teams/{team_id}/metadata/info"
    strip_fields(tok, project, path, keys)

def strip_club_metadata(tok, project, club_id, keys):
    path = f"projects/{project}/databases/(default)/documents/clubs/{club_id}/metadata/info"
    strip_fields(tok, project, path, keys)

if __name__ == "__main__":
    raise SystemExit(main())
