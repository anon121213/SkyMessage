"""Sends a text from the dev account mama_dev into her chat with artur_dev (local Development API), so UI checks can
watch an incoming message arrive. Usage: python3 tools/ui-shot/as-mama.py "text" [--base http://127.0.0.1:8080]
"""
import json, os, sys, urllib.request, uuid

base = sys.argv[sys.argv.index("--base") + 1].rstrip("/") if "--base" in sys.argv else "http://127.0.0.1:8080"
text = sys.argv[1] if len(sys.argv) > 1 and not sys.argv[1].startswith("--") else "Тестовое сообщение"
repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
accounts = {a["username"]: a for a in json.load(open(os.path.join(repo, "tools/e2e/dev-accounts.json")))["accounts"]}


def call(method, path, body=None, token=None):
    req = urllib.request.Request(base + path, method=method, data=json.dumps(body).encode() if body is not None else None,
                                 headers={"Content-Type": "application/json", **({"Authorization": "Bearer " + token} if token else {})})
    with urllib.request.urlopen(req, timeout=20) as resp:
        raw = resp.read()
        return json.loads(raw) if raw else None


auth = call("POST", "/api/auth/login", {"login": "mama_dev", "password": accounts["mama_dev"]["password"],
                                        "device": {"deviceName": "ui-shot", "platform": "Linux", "appVersion": "dev"}})
token = auth["accessToken"]
chat = call("GET", "/api/users/by-username/artur_dev", token=token)["privateChatId"]
msg = call("POST", f"/api/chats/{chat}/messages", {"clientMessageId": str(uuid.uuid4()), "text": text}, token)
call("POST", "/api/auth/logout", {}, token)
print("sent", msg["seq"])
