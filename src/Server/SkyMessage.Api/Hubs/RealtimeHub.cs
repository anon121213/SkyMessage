using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SkyMessage.Api.Infrastructure;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Identity;
using SkyMessage.Contracts;
using SkyMessage.Infrastructure.Identity;

namespace SkyMessage.Api.Hubs;

/// <summary>
/// The single real-time endpoint (<see cref="HubRoutes.Realtime"/>). Requires authentication via the
/// fallback policy. Each connection joins <see cref="SessionGroup"/> for its session so a revoked
/// session can be signed out instantly. Owned by the Identity feature (presence + typing).
/// </summary>
[Authorize]
public sealed class RealtimeHub(
    PresenceTracker presence,
    SessionConnectionRegistry connections,
    RealtimeIdentityService identity,
    IClock clock,
    ILogger<RealtimeHub> logger) : Hub<IRealtimeClient>, IRealtimeServer
{
    private const string UserIdItem = "sky.uid";
    private const string SessionIdItem = "sky.sid";
    private const string PresenceItem = "sky.presence";
    private const string TypingItem = "sky.typing";

    public static string SessionGroup(Guid sessionId) => $"session:{sessionId:N}";

    public override async Task OnConnectedAsync()
    {
        var (userId, sessionId) = HttpCurrentUser.FromPrincipal(Context.User);
        Context.Items[UserIdItem] = userId;
        Context.Items[SessionIdItem] = sessionId;

        // SignalR skips OnDisconnectedAsync when this method throws, so undo the registration ourselves.
        try
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(sessionId), Context.ConnectionAborted);
            connections.Add(sessionId, Context);

            // Uncached re-check: a revoke between authentication and registration would otherwise be missed.
            if (!await identity.OnConnectedAsync(sessionId, userId, Context.ConnectionAborted))
            {
                logger.LogDebug("Closing realtime connection of inactive session {SessionId}", sessionId);
                Context.Abort();
                return;
            }

            presence.Connected(userId, Context.ConnectionId);
            Context.Items[PresenceItem] = true;
            await base.OnConnectedAsync();
        }
        catch
        {
            Cleanup(userId, sessionId);
            throw;
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (TryGetIds(out var userId, out var sessionId))
        {
            var hadPresence = Context.Items.ContainsKey(PresenceItem);
            Cleanup(userId, sessionId);
            if (hadPresence)
            {
                try
                {
                    await identity.TouchSessionAsync(sessionId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not stamp activity of session {SessionId}", sessionId);
                }
            }
        }
        await base.OnDisconnectedAsync(exception);
    }

    public async Task SetActive(bool isActive)
    {
        if (!TryGetIds(out var userId, out var sessionId) || !Context.Items.ContainsKey(PresenceItem)) return;
        presence.SetActive(userId, Context.ConnectionId, isActive);
        if (!isActive) return;
        try
        {
            await identity.TouchSessionAsync(sessionId, Context.ConnectionAborted);
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not stamp activity of session {SessionId}", sessionId);
        }
    }

    public async Task SendTyping(Guid chatId, TypingAction action)
    {
        if (!TryGetIds(out var userId, out _) || !Enum.IsDefined(action)) return;

        if (!Context.Items.TryGetValue(TypingItem, out var item) || item is not TypingThrottle throttle)
        {
            throttle = new TypingThrottle();
            Context.Items[TypingItem] = throttle;
        }
        if (!throttle.TryAccept(action, clock.UtcNow)) return;

        try
        {
            var recipients = await identity.GetTypingRecipientsAsync(chatId, userId, Context.ConnectionAborted);
            if (recipients.Count == 0) return;
            await Clients.Users(recipients.Select(id => id.ToString()).ToList())
                .Typing(new TypingEvent { ChatId = chatId, UserId = userId, Action = action });
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Typing is best effort: never surface an error to the client for it.
            logger.LogWarning(ex, "Typing fan-out failed for chat {ChatId}", chatId);
        }
    }

    private void Cleanup(Guid userId, Guid sessionId)
    {
        connections.Remove(sessionId, Context.ConnectionId);
        if (Context.Items.Remove(PresenceItem))
            presence.Disconnected(userId, Context.ConnectionId);
    }

    private bool TryGetIds(out Guid userId, out Guid sessionId)
    {
        userId = Context.Items.TryGetValue(UserIdItem, out var uid) && uid is Guid u ? u : Guid.Empty;
        sessionId = Context.Items.TryGetValue(SessionIdItem, out var sid) && sid is Guid s ? s : Guid.Empty;
        return userId != Guid.Empty && sessionId != Guid.Empty;
    }
}
