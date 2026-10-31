"""Live end-to-end smoke test against a running SkyMessage API.

Usage: python3 tools/e2e/smoke.py            (SKY_BASE=http://host:port/ to target another server)
  SKY_INVITE=<code>   registration code when the server runs in InviteCode mode
  SKY_CLEANUP=1       delete the test accounts at the end (use against a real server)
Needs ffmpeg on PATH to generate the test photo and voice note.
"""
import base64, hashlib, hmac, json, os, subprocess, sys, tempfile, time, urllib.error, urllib.request, uuid

BASE = os.environ.get("SKY_BASE", "http://localhost:8080/")
INVITE = os.environ.get("SKY_INVITE")
OK, FAIL = 0, 0
CREATED = []  # (access token, password) of every account this run registered


def req(method, path, body=None, token=None, raw=None, headers=None, expect=None):
    url = BASE + path
    data = None
    h = {"Accept": "application/json"}
    if headers: h.update(headers)
    if body is not None:
        data = json.dumps(body).encode(); h["Content-Type"] = "application/json"
    if raw is not None:
        data = raw; h["Content-Type"] = "application/octet-stream"
    if token: h["Authorization"] = "Bearer " + token
    r = urllib.request.Request(url, data=data, method=method, headers=h)
    for attempt in range(3):
        try:
            with urllib.request.urlopen(r, timeout=20) as resp:
                content = resp.read()
                status = resp.status
                hdrs = dict(resp.headers)
            break
        except urllib.error.HTTPError as e:
            content = e.read(); status = e.code; hdrs = dict(e.headers)
            break
        except urllib.error.URLError as e:
            # A new TCP connection per request: over a lossy long-haul link a connect can stall. URLError comes from
            # the connect/send phase (read timeouts surface differently), so retrying cannot repeat a request.
            if attempt == 2: raise
            print(f"  (connection problem on {method} {path}: {e.reason}; retrying)")
    if expect is not None and status != expect:
        raise AssertionError(f"{method} {path} -> {status} (expected {expect}): {content[:300]!r}")
    ctype = hdrs.get("Content-Type", "")
    parsed = json.loads(content) if content and "json" in ctype else content
    return status, parsed, hdrs


def check(name, cond, detail=""):
    global OK, FAIL
    if cond: OK += 1; print(f"  ok   {name}")
    else: FAIL += 1; print(f"  FAIL {name} {detail}")


def device():
    return {"deviceName": "e2e", "platform": "Linux", "appVersion": "e2e"}


def register(prefix):
    u = "e2e_" + prefix + uuid.uuid4().hex[:8]
    body = {"username": u, "password": "correct-horse-battery", "displayName": prefix.capitalize(), "device": device()}
    if INVITE:
        body["inviteCode"] = INVITE
    _, auth, _ = req("POST", "api/auth/register", body, expect=200)
    CREATED.append((auth["accessToken"], "correct-horse-battery"))
    return auth


def cleanup():
    for token, password in CREATED:
        s, _, _ = req("POST", "api/users/me/delete", {"password": password}, token)
        print(f"  cleanup: delete account -> {s}")


def media_sig(key_b64url, session_id, media_id):
    key = base64.urlsafe_b64decode(key_b64url + "=" * (-len(key_b64url) % 4))
    mac = hmac.new(key, uuid.UUID(media_id).bytes, hashlib.sha256).digest()
    sig = base64.urlsafe_b64encode(mac).decode().rstrip("=")[:22]
    return f"{uuid.UUID(session_id).hex}.{sig}"


def upload(token, kind, path, ctype, **meta):
    data = open(path, "rb").read()
    _, up, _ = req("POST", "api/media/uploads", {"kind": kind, "fileName": os.path.basename(path), "contentType": ctype, "sizeBytes": len(data), **meta}, token, expect=200)
    cs = up["chunkSize"]
    for i in range(up["chunkCount"]):
        req("PUT", f"api/media/uploads/{up['mediaId']}/chunks/{i}", token=token, raw=data[i * cs:(i + 1) * cs], expect=204)
    req("POST", f"api/media/uploads/{up['mediaId']}/complete", token=token, expect=200)
    for _ in range(60):
        _, m, _ = req("GET", f"api/media/{up['mediaId']}", token=token, expect=200)
        if m["status"] in ("Ready", "Failed"): return m
        time.sleep(0.5)
    raise AssertionError("media processing timeout")


def main():
    for _ in range(90):
        try:
            if req("GET", "health")[0] == 200: break
        except Exception: pass
        time.sleep(1)
    print("server info")
    _, info, _ = req("GET", "api/server/info", expect=200)
    check("instance id", info["instanceId"] != "00000000-0000-0000-0000-000000000000")
    check("calls enabled", info["callsEnabled"] is True, info)

    print("auth")
    a, b = register("alice"), register("boris")
    ta, tb = a["accessToken"], b["accessToken"]
    _, me, _ = req("GET", "api/users/me", token=ta, expect=200)
    check("me has saved chat", me["savedMessagesChatId"] != "00000000-0000-0000-0000-000000000000")
    _, refreshed, _ = req("POST", "api/auth/refresh", {"refreshToken": a["refreshToken"]}, expect=200)
    check("refresh rotates", refreshed["refreshToken"] != a["refreshToken"] and refreshed["mediaKey"] == a["mediaKey"])
    ta = refreshed["accessToken"]
    s, _, _ = req("POST", "api/auth/login", {"login": b["user"]["username"], "password": "wrong-password-xx", "device": device()})
    check("wrong password 401", s == 401)

    print("search")
    _, found, _ = req("GET", f"api/users/search?q={a['user']['username'][:6]}&limit=10", token=tb, expect=200)
    check("username prefix search", any(u["id"] == a["user"]["id"] for u in found), found)

    print("chat + messages")
    _, chat, _ = req("POST", "api/chats/private", {"userId": b["user"]["id"]}, ta, expect=200)
    cid = chat["id"]
    s, chats_b, _ = req("GET", "api/chats", token=tb, expect=200)
    check("peer does not see empty chat", all(c["id"] != cid for c in chats_b))
    cmid = str(uuid.uuid4())
    _, msg, _ = req("POST", f"api/chats/{cid}/messages", {"clientMessageId": cmid, "text": f"Привет, мама! @{b['user']['username']}"}, ta, expect=200)
    check("message seq 1", msg["seq"] == 1, msg)
    _, again, _ = req("POST", f"api/chats/{cid}/messages", {"clientMessageId": cmid, "text": "dup"}, ta, expect=200)
    check("idempotent send", again["id"] == msg["id"])
    _, chats_b, _ = req("GET", "api/chats", token=tb, expect=200)
    cb = next((c for c in chats_b if c["id"] == cid), None)
    check("peer sees chat with unread + mention", cb is not None and cb["unreadCount"] == 1 and cb["unreadMentionCount"] == 1, cb)
    _, page, _ = req("GET", f"api/chats/{cid}/messages?limit=50", token=tb, expect=200)
    check("page has message + users", len(page["messages"]) == 1 and len(page["users"]) >= 1 and page["syncCursor"])
    req("POST", f"api/chats/{cid}/read", {"upToSeq": 1}, tb, expect=204)
    _, ca, _ = req("GET", f"api/chats/{cid}", token=ta, expect=200)
    check("read receipt visible to sender", ca["othersLastReadSeq"] == 1, ca)
    req("PUT", f"api/chats/{cid}/messages/{msg['id']}/reaction", {"emoji": "❤️"}, tb, expect=200)
    _, ch, _ = req("GET", f"api/chats/{cid}/messages/changes?cursor={page['syncCursor']}", token=ta, expect=200)
    check("changes include reaction", any(m["id"] == msg["id"] and m["reactions"] for m in ch["messages"]), ch)

    tmp = tempfile.mkdtemp()
    print("photo")
    jpg = os.path.join(tmp, "photo.jpg")
    subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=1280x720", "-frames:v", "1", jpg], check=True)
    m = upload(ta, "Photo", jpg, "image/jpeg")
    check("photo ready + thumbnail", m["status"] == "Ready" and m["hasThumbnail"] and m["width"] == 1280, m)
    _, pm, _ = req("POST", f"api/chats/{cid}/messages", {"clientMessageId": str(uuid.uuid4()), "text": "фото", "mediaIds": [m["id"]]}, ta, expect=200)
    check("photo message type", pm["type"] == "Photo", pm["type"])
    t = media_sig(b["mediaKey"], b["sessionId"], m["id"])
    s, body, h = req("GET", f"api/media/{m['id']}/content?t={t}", headers={"Range": "bytes=0-99"})
    check("signed range fetch 206", s == 206 and len(body) == 100 and h.get("Content-Range", "").startswith("bytes 0-99/"), (s, h))
    s, _, _ = req("GET", f"api/media/{m['id']}/content?t={t[:-2]}xx")
    check("bad signature rejected", s in (401, 404), s)
    c = register("carol")
    s, _, _ = req("GET", f"api/media/{m['id']}/content", token=c["accessToken"])
    check("outsider gets 404", s == 404, s)

    print("voice")
    ogg = os.path.join(tmp, "voice.webm")
    subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-c:a", "libopus", ogg], check=True)
    v = upload(ta, "Voice", ogg, "audio/webm")
    check("voice ready as m4a with waveform", v["status"] == "Ready" and v["contentType"] == "audio/mp4" and v["waveform"] and 2500 <= (v["durationMs"] or 0) <= 3500, v)

    print("calls")
    s, join, _ = req("POST", "api/calls", {"chatId": cid, "isVideo": False}, ta)
    check("start call", s == 200 and join["liveKitToken"] and join["call"]["status"] == "Ringing", (s, join))
    if s == 200:
        callid = join["call"]["id"]
        s, acc, _ = req("POST", f"api/calls/{callid}/accept", token=tb)
        check("accept call", s == 200 and acc["call"]["status"] == "Active", (s, acc))
        req("POST", f"api/calls/{callid}/hangup", token=ta)
        time.sleep(1)
        _, page2, _ = req("GET", f"api/chats/{cid}/messages?limit=50", token=tb, expect=200)
        check("call log posted", any(x["type"] == "Call" for x in page2["messages"]), [x["type"] for x in page2["messages"]])
        _, hist, _ = req("GET", "api/calls/history?limit=10", token=tb, expect=200)
        check("call history", len(hist) >= 1)
    s, _, _ = req("HEAD", "livekit")
    check("livekit proxy reachable", s == 200, s)

    print(f"\nRESULT: {OK} ok, {FAIL} failed")
    sys.exit(1 if FAIL else 0)


if __name__ == "__main__":
    try:
        main()
    finally:
        if os.environ.get("SKY_CLEANUP") == "1":
            cleanup()
