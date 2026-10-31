using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace SkyMessage.Api.IntegrationTests.Calls;

/// <summary>A Twirp RoomService call the API made, with the video grant of its (verified) server token.</summary>
internal sealed record TwirpCall(string Method, JsonElement Body, JsonElement? Grant, bool Authorized)
{
    public string? Room => Body.TryGetProperty("room", out var room) ? room.GetString() : null;
    public string? Identity => Body.TryGetProperty("identity", out var identity) ? identity.GetString() : null;
}

/// <summary>A request that reached LiveKit through the API's /livekit reverse proxy.</summary>
internal sealed record ProxiedRequest(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// A minimal stand-in for the LiveKit server on a loopback port: the Twirp RoomService the API calls (rooms and
/// participants are whatever the test puts in) and a catch-all that records what the /livekit proxy forwards.
/// </summary>
internal sealed class FakeLiveKit : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<string, (string Sid, string State)>> _rooms = new(StringComparer.Ordinal);
    private readonly List<TwirpCall> _twirp = [];
    private readonly List<ProxiedRequest> _proxied = [];
    private readonly List<(Func<TwirpCall, bool> Match, TaskCompletionSource<TwirpCall> Done)> _waiters = [];

    private FakeLiveKit(WebApplication app) => _app = app;

    public string Url { get; private set; } = "";

    /// <summary>False: every Twirp call fails like a LiveKit that is down (503 unavailable).</summary>
    public bool Available { get; set; } = true;

    /// <summary>
    /// ListRooms answers 503 unless enabled, so the API host's periodic reconciliation stays paused instead of
    /// treating every active call of the shared database as "room gone".
    /// </summary>
    public bool ListRoomsEnabled { get; set; }

    public static async Task<FakeLiveKit> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = "FakeLiveKit",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var fake = new FakeLiveKit(app);
        app.MapPost("/twirp/livekit.RoomService/{method}", (HttpContext ctx, string method) => fake.TwirpAsync(ctx, method));
        app.Map("/{**path}", (HttpContext ctx) => fake.Proxy(ctx));
        await app.StartAsync(CallKit.Ct);
        fake.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return fake;
    }

    // ---- room state ----

    public string AddParticipant(string room, string identity, string? sid = null)
    {
        sid ??= "PA_" + Guid.NewGuid().ToString("N")[..12];
        lock (_gate)
        {
            if (!_rooms.TryGetValue(room, out var participants)) _rooms[room] = participants = new Dictionary<string, (string, string)>();
            participants[identity] = (sid, "ACTIVE");
        }
        return sid;
    }

    public void RemoveParticipantFromRoom(string room, string identity)
    {
        lock (_gate)
        {
            if (_rooms.TryGetValue(room, out var participants)) participants.Remove(identity);
        }
    }

    public string? SidOf(string room, string identity)
    {
        lock (_gate)
            return _rooms.TryGetValue(room, out var participants) && participants.TryGetValue(identity, out var p) ? p.Sid : null;
    }

    public bool HasRoom(string room)
    {
        lock (_gate) return _rooms.ContainsKey(room);
    }

    // ---- what the API did ----

    public IReadOnlyList<TwirpCall> TwirpCalls(string? method = null, string? room = null)
    {
        lock (_gate)
            return _twirp.Where(c => (method is null || c.Method == method) && (room is null || c.Room == room)).ToList();
    }

    public IReadOnlyList<ProxiedRequest> Proxied
    {
        get
        {
            lock (_gate) return _proxied.ToList();
        }
    }

    /// <summary>
    /// Waits for the <paramref name="occurrence"/>th matching Twirp call (many are fire-and-forget in the API: room
    /// cleanup, kicks). <paramref name="match"/> runs under the fake's lock: it must not call back into the fake.
    /// </summary>
    public async Task<TwirpCall> WaitForTwirpAsync(string method, Func<TwirpCall, bool>? match = null, int occurrence = 1)
    {
        Task<TwirpCall> pending;
        lock (_gate)
        {
            var seen = _twirp.Where(c => c.Method == method && (match?.Invoke(c) ?? true)).ToList();
            if (seen.Count >= occurrence) return seen[occurrence - 1];
            var count = seen.Count;
            var done = new TaskCompletionSource<TwirpCall>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((c => c.Method == method && (match?.Invoke(c) ?? true) && ++count == occurrence, done));
            pending = done.Task;
        }
        try
        {
            return await pending.WaitAsync(CallKit.EventTimeout, CallKit.Ct);
        }
        catch (TimeoutException)
        {
            throw new ShouldAssertException(
                $"LiveKit got no matching {method}. Got: {string.Join("; ", TwirpCalls().Select(c => $"{c.Method} {c.Body.GetRawText()}"))}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
    }

    // ---- endpoints ----

    private async Task<IResult> TwirpAsync(HttpContext ctx, string method)
    {
        JsonElement body;
        using (var json = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted))
            body = json.RootElement.Clone();

        var header = ctx.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : "";
        var authorized = token.Length > 0 && await Jwt.IsSignedByDevKeyAsync(token);
        JsonElement? grant = authorized && Jwt.Payload(token).TryGetProperty("video", out var video) ? video : null;
        var call = new TwirpCall(method, body, grant, authorized);

        IResult result;
        lock (_gate)
        {
            _twirp.Add(call);
            result = !Available || (method == "ListRooms" && !ListRoomsEnabled)
                ? Error(503, "unavailable")
                : !authorized
                    ? Error(401, "unauthenticated")
                    : Answer(call);
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (!_waiters[i].Match(call)) continue;
                _waiters[i].Done.TrySetResult(call);
                _waiters.RemoveAt(i);
            }
        }
        return result;
    }

    /// <summary>Answers like LiveKit's Twirp JSON (protobuf field names); unknown rooms/participants are not_found. Under the lock.</summary>
    private IResult Answer(TwirpCall call)
    {
        switch (call.Method)
        {
            case "ListRooms":
            {
                var names = call.Body.TryGetProperty("names", out var n) && n.ValueKind == JsonValueKind.Array
                    ? n.EnumerateArray().Select(x => x.GetString()).ToHashSet()
                    : null;
                var rooms = _rooms
                    .Where(r => names is null || names.Contains(r.Key))
                    .Select(r => new { sid = "RM_" + r.Key, name = r.Key, num_participants = r.Value.Count })
                    .ToList();
                return Results.Json(new { rooms });
            }
            case "ListParticipants":
            {
                if (call.Room is not { } room || !_rooms.TryGetValue(room, out var participants)) return Error(404, "not_found");
                return Results.Json(new
                {
                    participants = participants.Select(p => new { sid = p.Value.Sid, identity = p.Key, state = p.Value.State }).ToList(),
                });
            }
            case "DeleteRoom":
                return call.Room is { } deleted && _rooms.Remove(deleted) ? Results.Json(new { }) : Error(404, "not_found");
            case "RemoveParticipant":
                return call.Room is { } from && call.Identity is { } identity && _rooms.TryGetValue(from, out var members) && members.Remove(identity)
                    ? Results.Json(new { })
                    : Error(404, "not_found");
            default:
                return Error(404, "bad_route");
        }
    }

    private static IResult Error(int status, string code) => Results.Json(new { code, msg = "fake livekit: " + code }, statusCode: status);

    private IResult Proxy(HttpContext ctx)
    {
        var request = new ProxiedRequest(
            ctx.Request.Method,
            ctx.Request.Path.Value ?? "",
            ctx.Request.QueryString.Value ?? "",
            ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase));
        lock (_gate) _proxied.Add(request);
        // LiveKit answers with its own permissive CORS headers.
        ctx.Response.Headers.AccessControlAllowOrigin = "*";
        return Results.Text("fake-livekit");
    }
}
