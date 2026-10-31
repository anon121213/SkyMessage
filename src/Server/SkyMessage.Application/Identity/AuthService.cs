using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Options;
using SkyMessage.Contracts;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Identity;

/// <summary>Registration, sign-in, token refresh, sign-out, username availability, password change.</summary>
public sealed class AuthService(
    IAppDbContext db,
    IUserPasswordHasher passwords,
    IAccessTokenIssuer accessTokens,
    LoginThrottle throttle,
    SavedChatProvider savedChats,
    SessionRevoker revoker,
    IClock clock,
    IOptions<ServerOptions> serverOptions,
    IOptions<JwtOptions> jwtOptions,
    ILogger<AuthService> logger)
{
    private const int MaxRefreshAttempts = 3;

    private TimeSpan RefreshLifetime => TimeSpan.FromDays(Math.Max(1, jwtOptions.Value.RefreshTokenDays));

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, string? ipAddress, CancellationToken ct)
    {
        EnsureRegistrationAllowed(request.InviteCode);

        var username = UsernameRules.Clean(request.Username);
        if (UsernameRules.Validate(username) is { } usernameError)
            throw new BadRequestException(usernameError);
        if (request.Password.Length < Limits.PasswordMinLength)
            throw new BadRequestException(ErrorCodes.PasswordTooWeak);
        var displayName = request.DisplayName.Trim();
        if (displayName.Length is 0 or > Limits.DisplayNameMaxLength)
            throw new BadRequestException(ErrorCodes.Validation);

        var normalized = User.Normalize(username);
        if (await db.Users.AnyAsync(u => u.UsernameNormalized == normalized, ct))
            throw new ConflictException(ErrorCodes.UsernameTaken);

        var now = clock.UtcNow;
        var user = new User
        {
            Username = username,
            UsernameNormalized = normalized,
            DisplayName = displayName,
            CreatedAt = now,
            UpdatedAt = now,
        };
        user.PasswordHash = passwords.Hash(user, request.Password);
        var savedChat = SavedChatProvider.Create(user.Id, now);
        var (session, refreshToken) = NewSession(user.Id, request.Device, ipAddress, now);

        db.Users.Add(user);
        db.Chats.Add(savedChat);
        db.Sessions.Add(session);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost the race for the username (unique index).
            db.ChangeTracker.Clear();
            if (await db.Users.AnyAsync(u => u.UsernameNormalized == normalized, ct))
                throw new ConflictException(ErrorCodes.UsernameTaken);
            throw;
        }

        savedChats.Remember(user.Id, savedChat.Id);
        logger.LogInformation("User {UserId} registered, session {SessionId}", user.Id, session.Id);
        return Respond(user, session.Id, session.MediaKey, refreshToken, session.RefreshTokenExpiresAt, savedChat.Id, now);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, string? ipAddress, CancellationToken ct)
    {
        var login = request.Login.Trim();
        string throttleKey;
        User? user;
        if (PhoneNormalizer.LooksLikePhone(login))
        {
            var candidates = PhoneNormalizer.LoginCandidates(login, request.DefaultRegion);
            throttleKey = LoginThrottle.PhoneKey(candidates.Count > 0 ? candidates[0] : new string(login.Where(char.IsAsciiDigit).ToArray()));
            if (throttle.IsLockedOut(throttleKey))
                throw new RateLimitedException();
            user = await FindByPhoneAsync(candidates, ct);
        }
        else
        {
            var normalized = User.Normalize(login);
            throttleKey = LoginThrottle.UsernameKey(normalized);
            if (throttle.IsLockedOut(throttleKey))
                throw new RateLimitedException();
            user = normalized.Length is 0 or > Limits.UsernameMaxLength
                ? null
                : await db.Users.FirstOrDefaultAsync(u => u.UsernameNormalized == normalized && !u.IsDeleted, ct);
        }

        // Same work and the same answer whether the user does not exist or the password is wrong.
        PasswordCheckResult check;
        if (user is null)
        {
            passwords.VerifyDummy(request.Password);
            check = PasswordCheckResult.Failed;
        }
        else
        {
            check = passwords.Verify(user, user.PasswordHash, request.Password);
        }

        if (user is null || check == PasswordCheckResult.Failed)
        {
            throttle.RegisterFailure(throttleKey);
            throw new UnauthorizedException(ErrorCodes.InvalidCredentials);
        }
        throttle.Reset(throttleKey);

        var now = clock.UtcNow;
        if (check == PasswordCheckResult.SuccessRehashNeeded)
            user.PasswordHash = passwords.Hash(user, request.Password);
        var (session, refreshToken) = NewSession(user.Id, request.Device, ipAddress, now);
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);

        var savedChatId = await savedChats.GetIdAsync(user.Id, ct);
        logger.LogInformation("User {UserId} signed in, session {SessionId}", user.Id, session.Id);
        return Respond(user, session.Id, session.MediaKey, refreshToken, session.RefreshTokenExpiresAt, savedChatId, now);
    }

    public async Task<AuthResponse> RefreshAsync(RefreshRequest request, string? ipAddress, CancellationToken ct)
    {
        var token = request.RefreshToken.Trim();
        if (token.Length is 0 or > RefreshTokens.MaxLength)
            throw InvalidRefreshToken();
        var presentedHash = RefreshTokens.Hash(token);
        var ip = IdentityMapping.Clip(ipAddress, SessionFieldLimits.IpAddress);

        for (var attempt = 0; attempt < MaxRefreshAttempts; attempt++)
        {
            var session = await db.Sessions.AsNoTracking()
                .Where(s => s.RefreshTokenHash == presentedHash || s.PreviousRefreshTokenHash == presentedHash)
                .Select(s => new
                {
                    s.Id, s.UserId, s.RefreshTokenHash, s.PreviousRefreshTokenHash, s.RefreshTokenRotatedAt,
                    s.RefreshTokenConfirmedAt, s.RefreshTokenExpiresAt, s.RevokedAt, s.MediaKey,
                })
                .FirstOrDefaultAsync(ct);
            if (session is null)
                throw InvalidRefreshToken();

            var now = clock.UtcNow;
            var state = new RefreshTokenState(session.RefreshTokenHash, session.PreviousRefreshTokenHash,
                session.RefreshTokenRotatedAt, session.RefreshTokenExpiresAt, session.RevokedAt, session.RefreshTokenConfirmedAt);
            var decision = RefreshTokenPolicy.Decide(presentedHash, state, now);
            switch (decision)
            {
                case RefreshDecision.Invalid:
                    throw InvalidRefreshToken();
                case RefreshDecision.Theft:
                    logger.LogWarning("Rotated refresh token replayed for session {SessionId} of user {UserId}; revoking the session",
                        session.Id, session.UserId);
                    await revoker.RevokeAsync(s => s.Id == session.Id, ct);
                    throw InvalidRefreshToken();
            }

            // Rotate: the successor of the current token. Retry (the previous token again): the same successor the
            // lost response carried, so every attempt and every concurrent request gets one and the same token.
            var refreshToken = RefreshTokens.Successor(RefreshTokens.SuccessorKey(jwtOptions.Value.SigningKey), session.Id, token);
            var newHash = RefreshTokens.Hash(refreshToken);
            var current = session.RefreshTokenHash;
            var expiresAt = session.RefreshTokenExpiresAt;
            int updated;
            if (decision == RefreshDecision.Retry && newHash == current)
            {
                updated = await db.Sessions
                    .Where(s => s.Id == session.Id && s.RefreshTokenHash == current && s.RevokedAt == null)
                    .ExecuteUpdateAsync(s =>
                    {
                        s.SetProperty(x => x.LastActiveAt, now);
                        if (ip is not null)
                            s.SetProperty(x => x.IpAddress, ip);
                    }, ct);
            }
            else
            {
                // Rotate, or a retry whose stored successor came from elsewhere (sessions rotated before successors
                // were derived, a changed signing key): the previous token stays the one the client holds.
                var previous = decision == RefreshDecision.Rotate ? current : presentedHash;
                expiresAt = now + RefreshLifetime;
                // Compare-and-swap on the current hash: if a concurrent refresh rotated first, nothing is
                // updated and the loop re-evaluates the presented token against the new state.
                updated = await db.Sessions
                    .Where(s => s.Id == session.Id && s.RefreshTokenHash == current && s.RevokedAt == null)
                    .ExecuteUpdateAsync(s =>
                    {
                        s.SetProperty(x => x.RefreshTokenHash, newHash);
                        s.SetProperty(x => x.PreviousRefreshTokenHash, previous);
                        s.SetProperty(x => x.RefreshTokenRotatedAt, now);
                        s.SetProperty(x => x.RefreshTokenConfirmedAt, (DateTimeOffset?)null);
                        s.SetProperty(x => x.RefreshTokenExpiresAt, expiresAt);
                        s.SetProperty(x => x.LastActiveAt, now);
                        if (ip is not null)
                            s.SetProperty(x => x.IpAddress, ip);
                    }, ct);
            }
            if (updated == 0)
                continue;

            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == session.UserId && !u.IsDeleted, ct);
            if (user is null)
            {
                await revoker.RevokeAsync(s => s.Id == session.Id, ct);
                throw InvalidRefreshToken();
            }

            var savedChatId = await savedChats.GetIdAsync(user.Id, ct);
            return Respond(user, session.Id, session.MediaKey, refreshToken, expiresAt, savedChatId, now);
        }

        throw InvalidRefreshToken();
    }

    public Task LogoutAsync(Guid userId, Guid sessionId, CancellationToken ct) =>
        revoker.RevokeAsync(s => s.Id == sessionId && s.UserId == userId, ct);

    /// <param name="currentUserId">Signed-in caller (settings screen): their own username counts as available.</param>
    public async Task<UsernameAvailabilityDto> CheckUsernameAsync(string? username, Guid? currentUserId, CancellationToken ct)
    {
        var cleaned = UsernameRules.Clean(username);
        if (UsernameRules.Validate(cleaned) is { } error)
            return new UsernameAvailabilityDto { Username = cleaned, Available = false, ErrorCode = error };

        var normalized = User.Normalize(cleaned);
        var ownerId = await db.Users.AsNoTracking()
            .Where(u => u.UsernameNormalized == normalized)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);
        var available = ownerId is null || ownerId == currentUserId;
        return new UsernameAvailabilityDto
        {
            Username = cleaned,
            Available = available,
            ErrorCode = available ? null : ErrorCodes.UsernameTaken,
        };
    }

    public async Task ChangePasswordAsync(Guid userId, Guid sessionId, ChangePasswordRequest request, CancellationToken ct)
    {
        if (request.NewPassword.Length < Limits.PasswordMinLength)
            throw new BadRequestException(ErrorCodes.PasswordTooWeak);

        var throttleKey = LoginThrottle.PasswordKey(userId);
        if (throttle.IsLockedOut(throttleKey))
            throw new RateLimitedException();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, ct)
                   ?? throw new NotFoundException(ErrorCodes.UserNotFound);
        if (passwords.Verify(user, user.PasswordHash, request.CurrentPassword) == PasswordCheckResult.Failed)
        {
            throttle.RegisterFailure(throttleKey);
            throw new BadRequestException(ErrorCodes.WrongPassword);
        }
        throttle.Reset(throttleKey);

        user.PasswordHash = passwords.Hash(user, request.NewPassword);
        user.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        if (request.TerminateOtherSessions)
            await revoker.RevokeAsync(s => s.UserId == userId && s.Id != sessionId, ct);
        logger.LogInformation("User {UserId} changed the password", userId);
    }

    private void EnsureRegistrationAllowed(string? inviteCode)
    {
        var options = serverOptions.Value;
        switch (options.RegistrationMode)
        {
            case RegistrationMode.Open:
                return;
            case RegistrationMode.InviteCode when string.IsNullOrEmpty(options.RegistrationCode):
                logger.LogWarning("Server:RegistrationMode is InviteCode but Server:RegistrationCode is empty; nobody can register");
                break;
            case RegistrationMode.InviteCode when CodesEqual(options.RegistrationCode!, inviteCode):
                return;
        }
        throw new ForbiddenException(ErrorCodes.RegistrationClosed);
    }

    // Hashing both sides first makes the comparison constant-time regardless of the code length.
    private static bool CodesEqual(string expected, string? provided) =>
        provided is not null
        && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected.Trim())),
            SHA256.HashData(Encoding.UTF8.GetBytes(provided.Trim())));

    private async Task<User?> FindByPhoneAsync(IReadOnlyList<string> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0) return null;
        var numbers = candidates.ToList();
        var users = await db.Users
            .Where(u => u.PhoneNumber != null && numbers.Contains(u.PhoneNumber) && !u.IsDeleted)
            .ToListAsync(ct);
        // Prefer the number as parsed in the client's region over the RU fallback.
        return numbers.Select(n => users.FirstOrDefault(u => u.PhoneNumber == n)).FirstOrDefault(u => u is not null);
    }

    private (Session Session, string RefreshToken) NewSession(Guid userId, DeviceInfoDto? device, string? ipAddress, DateTimeOffset now)
    {
        device ??= new DeviceInfoDto();
        var refreshToken = RefreshTokens.Generate();
        var session = new Session
        {
            UserId = userId,
            RefreshTokenHash = RefreshTokens.Hash(refreshToken),
            RefreshTokenExpiresAt = now + RefreshLifetime,
            MediaKey = MediaSignature.NewKey(),
            DeviceName = IdentityMapping.Clip(device.DeviceName, Limits.DeviceNameMaxLength) ?? "",
            Platform = Enum.IsDefined(device.Platform) ? device.Platform : ClientPlatform.Unknown,
            AppVersion = IdentityMapping.Clip(device.AppVersion, SessionFieldLimits.AppVersion) ?? "",
            OsVersion = string.IsNullOrWhiteSpace(device.OsVersion) ? null : IdentityMapping.Clip(device.OsVersion, SessionFieldLimits.OsVersion),
            IpAddress = IdentityMapping.Clip(ipAddress, SessionFieldLimits.IpAddress),
            CreatedAt = now,
            LastActiveAt = now,
        };
        return (session, refreshToken);
    }

    private AuthResponse Respond(User user, Guid sessionId, byte[] mediaKey, string refreshToken, DateTimeOffset refreshExpiresAt, Guid savedChatId, DateTimeOffset now)
    {
        var access = accessTokens.Issue(user.Id, user.Username, sessionId, now, RefreshTokens.Binding(RefreshTokens.Hash(refreshToken)));
        return new AuthResponse
        {
            SessionId = sessionId,
            AccessToken = access.Token,
            AccessTokenExpiresAt = access.ExpiresAt,
            RefreshToken = refreshToken,
            RefreshTokenExpiresAt = refreshExpiresAt,
            MediaKey = MediaSignature.EncodeKey(mediaKey),
            User = user.ToMeDto(savedChatId),
        };
    }

    private static UnauthorizedException InvalidRefreshToken() => new(ErrorCodes.RefreshTokenInvalid);
}
