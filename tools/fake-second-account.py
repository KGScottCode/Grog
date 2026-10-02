#!/usr/bin/env python3
# DEV TOOL, never shipped: fakes a second GOG account in the manifest so multi-account UI can be
# tested without owning one. It registers an account row and adds it as a per-file owner on a slice
# of the library. Fully reversible with --undo. Run with Grog CLOSED (it rewrites grog-manifest.json).
#
#   python tools/fake-second-account.py                 # add 'kids' as co-owner of every 3rd game
#   python tools/fake-second-account.py --every 2       # denser co-ownership
#   python tools/fake-second-account.py --id sis --name "SisterAccount"
#   python tools/fake-second-account.py --undo          # remove the fake account + its ownerships
#
# The manifest path is resolved the same way Grog does: %APPDATA%/Grog on Windows,
# ~/.config/Grog elsewhere, overridable with GROG_CONFIG_DIR.

import argparse, json, os, shutil, sys
from pathlib import Path

def config_dir() -> Path:
    if env := os.environ.get("GROG_CONFIG_DIR"):
        return Path(env)
    if sys.platform == "win32":
        return Path(os.environ["APPDATA"]) / "Grog"
    return Path.home() / ".config" / "Grog"

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--id", default="kids", help="fake account id (lowercase slug)")
    ap.add_argument("--name", default="KidsAccount", help="fake account display username")
    ap.add_argument("--every", type=int, default=3, help="co-own every Nth game (default 3)")
    ap.add_argument("--undo", action="store_true", help="remove the fake account and its ownerships")
    args = ap.parse_args()
    acct = args.id.lower()

    path = config_dir() / "grog-manifest.json"
    if not path.exists():
        sys.exit(f"manifest not found: {path}")
    backup = path.with_suffix(".json.fakebak")
    if not args.undo and not backup.exists():
        shutil.copy2(path, backup)   # first run keeps a pristine copy beside the manifest

    m = json.loads(path.read_text(encoding="utf-8"))
    accounts = m.setdefault("Accounts", [])
    items = m.get("Items", [])
    touched = 0

    if args.undo:
        accounts[:] = [a for a in accounts if a.get("Id") != acct]
        for it in items:
            for f in it.get("Files", []) + it.get("OldVersionFiles", []):
                owners = f.get("OwnerIds")
                if owners and acct in owners:
                    owners.remove(acct); touched += 1
            if it.get("AccountId") == acct:
                it["AccountId"] = ""
        print(f"removed account '{acct}' and {touched} file ownerships")
    else:
        if not any(a.get("Id") == acct for a in accounts):
            accounts.append({"Id": acct, "Username": args.name, "Login": args.name})
        for i, it in enumerate(items):
            if i % args.every != 0:
                continue
            for f in it.get("Files", []):
                owners = f.setdefault("OwnerIds", [])
                if acct not in owners:
                    owners.append(acct); touched += 1
        print(f"account '{acct}' registered; co-owns files on every {args.every}rd game ({touched} files)")
        print("note: no tokens-%s.json exists, so the account card will read 'signed out' - correct," % acct)
        print("      and useful for testing the signed-out treatments.")

    path.write_text(json.dumps(m, indent=2), encoding="utf-8")
    print(f"manifest rewritten: {path}")
    if backup.exists():
        print(f"pristine copy kept at: {backup}")

if __name__ == "__main__":
    main()
