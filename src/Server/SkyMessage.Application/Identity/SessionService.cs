using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Contracts;

namespace SkyMessage.Application.Identity;

/// <summary>The signed-in devices of a user and their push registration.</summary>
public sealed class SessionService(IAppDbContext db, SessionRevoker revoker, IClock clock)
{
    public async Task<IReadOnlyList<SessionDto>> ListAsync(Guid userId, Guid currentSessionId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var sessions = await db.Sessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.RefreshTokenExpiresAt > now)
            .OrderByDescending(s => s.LastActiveAt)
            .Select(s => new SessionDto
            {
                Id = s.Id,
                DeviceName = s.DeviceName,
                Platform = s.Platform,
                AppVersion = s.AppVersion,
                OsVersion = s.OsVersion,
                IpAddress = s.IpAddress,
                CreatedAt = s.CreatedAt,
                LastActiveAt = s.LastActiveAt,
                IsCurrent = s.Id == currentSessionId,
            })
            .ToListAsync(ct);
        // Current device first, the rest by recent activity (OrderBy is stable).
        return sessions.OrderByDescending(s => s.IsCurrent).ToList();
    }

    /// <summary>Idempotent for the owner's own sessions; someone else's (or unknown) session is 404.</summary>
    public async Task TerminateAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        if (!await db.Sessions.AnyAsync(s => s.Id == sessionId && s.UserId == userId, ct))
            throw new NotFoundException();
        await revoker.RevokeAsync(s => s.Id == sessionId && s.UserId == userId, ct);
    }

    public Task TerminateOthersAsync(Guid userId, Guid currentSessionId, CancellationToken ct) =>
        revoker.RevokeAsync(s => s.UserId == userId && s.Id != currentSessionId, ct);

    /// <summary>Stores the device's push registration (delivery itself arrives in v2).</summary>
    public async Task RegisterPushAsync(Guid userId, Guid sessionId, RegisterPushRequest request, CancellationToken ct)
    {
        var provider = request.Provider;
        var token = provider == PushProvider.None ? null : request.Token.Trim();
        var key = token is null || string.IsNullOrWhiteSpace(request.EncryptionKey)
            ? null
            : Convert.FromBase64String(request.EncryptionKey.Trim());
        if (token is null) provider = PushProvider.None;

        if (token is not null)
        {
            // A device token belongs to one installation: drop it from older sessions (e.g. after re-login).
            await db.Sessions
                .Where(s => s.Id != sessionId && s.PushProvider == provider && s.PushToken == token)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.PushProvider, PushProvider.None)
                    .SetProperty(x => x.PushToken, (string?)null)
                    .SetProperty(x => x.PushEncryptionKey, (byte[]?)null), ct);
        }

        var updated = await db.Sessions
            .Where(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.PushProvider, provider)
                .SetProperty(x => x.PushToken, token)
                .SetProperty(x => x.PushEncryptionKey, key), ct);
        if (updated == 0)
            throw new UnauthorizedException(ErrorCodes.SessionRevoked);
    }
}
