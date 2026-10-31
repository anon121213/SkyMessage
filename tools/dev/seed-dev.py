"""Seeds a fresh local Development server (its own database) with the dev accounts from tools/e2e/dev-accounts.json
and a short artur_dev <-> mama_dev conversation, so UI work can be checked right away. Safe to run again: existing
accounts are logged in instead of registered, and messages are only sent when the chat is empty.

    python3 tools/dev/seed-dev.py --base http://127.0.0.1:8091

Local Development servers only — never point it at a real server.
"""
import json
import os
import sys
import urllib.error
import urllib.request
import uuid

base = sys.argv[sys.argv.index("--base") + 1].rstrip("/") if "--base" in sys.argv else "http://127.0.0.1:8080"
if not any(h in base for h in ("127.0.0.1", "localhost", "10.0.2.2")):
    sys.exit("seed-dev.py only talks to local servers")
repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
accounts = json.load(open(os.path.join(repo, "tools/e2e/dev-accounts.json")))["accounts"]
device = {"deviceName": "seed-dev", "platform": "Linux", "appVersion": "dev"}


def call(method, path, body=None, token=None):
    req = urllib.request.Request(base + "/" + path.lstrip("/"), method=method,
                                 data=json.dumps(body).encode() if body is not None else None,
                                 headers={"Content-Type": "application/json", **({"Authorization": "Bearer " + token} if token else {})})
    with urllib.request.urlopen(req, timeout=20) as resp:
        raw = resp.read()
        return json.loads(raw) if raw else None


def sign_in(account):
    try:
        return call("POST", "api/auth/register", {"username": account["username"], "password": account["password"],
                                                  "displayName": account["displayName"], "device": device})
    except urllib.error.HTTPError:
        return call("POST", "api/auth/login", {"login": account["username"], "password": account["password"], "device": device})


auth = {a["username"]: sign_in(a) for a in accounts}
artur, mama = auth["artur_dev"], auth["mama_dev"]
chat = call("POST", "api/chats/private", {"userId": artur["user"]["id"]}, mama["accessToken"])
chat_id = chat["id"]
history = call("GET", f"api/chats/{chat_id}/messages?limit=1", token=mama["accessToken"]) or []
if not (history.get("items") if isinstance(history, dict) else history):
    for who, text in [(mama, "Привет, сынок! Как ты там?"), (artur, "Привет, мам! Всё хорошо, скоро позвоню"),
                      (mama, "Жду! Пирог испекла 🥧")]:
        call("POST", f"api/chats/{chat_id}/messages", {"clientMessageId": str(uuid.uuid4()), "text": text}, who["accessToken"])
for a in auth.values():
    try:
        call("POST", "api/auth/logout", {}, a["accessToken"])
    except urllib.error.HTTPError:
        pass
print("seeded", base, "chat", chat_id)
