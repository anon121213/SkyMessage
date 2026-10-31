#!/usr/bin/env python3
"""Writes latest.json, the manifest of an Android release that the server reads from its updates directory
(src/Server/SkyMessage.Application/Updates/ReleaseManifest.cs) and offers through GET api/app/version.

    python3 -I deploy/vps/release-manifest.py --apk dist/SkyMessage-0.2.57.apk --version-code 57 \
        --version-name 0.2.57 [--min-version-code 0] [--notes "..."] --out dist/latest.json

The APK keeps its name on the server, so it must be a plain "SkyMessage-<version>.apk".
"""
import argparse
import datetime
import hashlib
import json
import os
import re
import sys


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--apk", required=True)
    parser.add_argument("--version-code", type=int, required=True)
    parser.add_argument("--version-name", required=True)
    parser.add_argument("--min-version-code", type=int, default=0,
                        help="installed builds below this must update (the server no longer supports them)")
    parser.add_argument("--notes", default="")
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    name = os.path.basename(args.apk)
    if not re.fullmatch(r"SkyMessage-[A-Za-z0-9._-]+\.apk", name) or ".." in name:
        print(f"unexpected APK file name: {name}", file=sys.stderr)
        return 1
    if args.version_code <= 0:
        print("the version code must be positive", file=sys.stderr)
        return 1
    if not 0 <= args.min_version_code <= args.version_code:
        print("the minimum version code must be between 0 and the version code", file=sys.stderr)
        return 1

    digest = hashlib.sha256()
    size = 0
    with open(args.apk, "rb") as apk:
        for chunk in iter(lambda: apk.read(1 << 20), b""):
            digest.update(chunk)
            size += len(chunk)
    if size == 0:
        print("the APK is empty", file=sys.stderr)
        return 1

    manifest = {
        "android": {
            "versionCode": args.version_code,
            "versionName": args.version_name,
            "file": name,
            "sha256": digest.hexdigest(),
            "size": size,
            "notes": args.notes.strip()[:2000],
            "minVersionCode": args.min_version_code,
            "publishedAt": datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        }
    }
    temp = args.out + ".tmp"
    with open(temp, "w", encoding="utf-8") as out:
        json.dump(manifest, out, ensure_ascii=False, indent=2)
        out.write("\n")
    os.replace(temp, args.out)
    print(json.dumps(manifest["android"], ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
