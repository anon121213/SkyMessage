using Microsoft.Extensions.Caching.Memory;
using Shouldly;
using SkyMessage.Application.Identity;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.UnitTests.Identity;

public sealed class SessionValidatorTests : IDisposable
{
    private readonly FakeSnapshotStore _store = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly Guid _sessionId = Guid.CreateVersion7();
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _mediaId = Guid.CreateVersion7();
    private readonly byte[] _key = MediaSignature.NewKey();

    private SessionValidator Validator => new(_store, _cache);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _cache.Dispose();

    [Fact]
    public async Task Valid_signature_returns_the_session_owner()
    {
        _store.Sessions[_sessionId] = new SessionSnapshot(_userId, true, _key);
        var token = MediaSignature.Create(_key, _sessionId, _mediaId);

        var result = await Validator.ValidateMediaSignatureAsync(token, _mediaId, Ct);

        result.ShouldBe((_userId, _sessionId));
    }

    [Fact]
    public async Task Encoded_key_from_the_auth_response_produces_valid_signatures()
    {
        _store.Sessions[_sessionId] = new SessionSnapshot(_userId, true, _key);
        var token = MediaSignature.Create(MediaSignature.EncodeKey(_key), _sessionId, _mediaId);

        (await Validator.ValidateMediaSignatureAsync(token, _mediaId, Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Signature_for_another_file_is_rejected()
    {
        _store.Sessions[_sessionId] = new SessionSnapshot(_userId, true, _key);
        var token = MediaSignature.Create(_key, _sessionId, Guid.CreateVersion7());

        (await Validator.ValidateMediaSignatureAsync(token, _mediaId, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Signature_made_with_another_key_is_rejected()
    {
        _store.Sessions[_sessionId] = new SessionSnapshot(_userId, true, _key);
        var token = MediaSignature.Create(MediaSignature.NewKey(), _sessionId, _mediaId);

        (await Validator.ValidateMediaSignatureAsync(token, _mediaId, Ct)).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("not-a-guid-at-all-not-a-guid-at-.AAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Malformed_tokens_are_rejected_without_a_lookup(string token)
    {
        (await Validator.ValidateMediaSignatureAsync(token, _mediaId, Ct)).ShouldBeNull();
        _store.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Revoked_session_is_rejected()
    {
        _store.Sessions[_sessionId] = new SessionSnapshot(_userId, false, _key);
        var token = MediaSignature.Create(_key, _sessionId, _mediaId);

        (await Validator.ValidateMediaSignatureAsync(token, _mediaId, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Unknown_sessions_are_not_cached()
    {
        var token = MediaSignature.Create(_key, _sessionId, _mediaId);

        (await Validator.ValidateMediaSignatureAsync(token, _mediaId, Ct)).ShouldBeNull();
        (await Validator.ValidateMediaSignatureAsync(token, _mediaId, Ct)).ShouldBeNull();

        _store.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Session_key_is_cached_between_validations()
    {
        _store.Sessions[_sessionId] = new SessionSnapshot(_userId, true, _key);

        await Validator.ValidateMediaSignatureAsync(MediaSignature.Create(_key, _sessionId, _mediaId), _mediaId, Ct);
        var other = Guid.CreateVersion7();
        (await Validator.ValidateMediaSignatureAsync(MediaSignature.Create(_key, _sessionId, other), other, Ct)).ShouldNotBeNull();

        _store.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Eviction_makes_a_revocation_effective_immediately()
    {
        _store.Sessions[_sessionId] = new SessionSnapshot(_userId, true, _key);
        var token = MediaSignature.Create(_key, _sessionId, _mediaId);
        (await Validator.ValidateMediaSignatureAsync(token, _mediaId, Ct)).ShouldNotBeNull();
        (await Validator.IsActiveAsync(_sessionId, _userId, Ct)).ShouldBeTrue();

        _store.Sessions[_sessionId] = new SessionSnapshot(_userId, false, _key);
        SessionValidator.Evict(_cache, _sessionId);

        (await Validator.ValidateMediaSignatureAsync(token, _mediaId, Ct)).ShouldBeNull();
        (await Validator.IsActiveAsync(_sessionId, _userId, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Active_session_must_belong_to_the_token_subject()
    {
        _store.Sessions[_sessionId] = new SessionSnapshot(_userId, true, _key);

        (await Validator.IsActiveAsync(_sessionId, _userId, Ct)).ShouldBeTrue();
        (await Validator.IsActiveAsync(_sessionId, Guid.CreateVersion7(), Ct)).ShouldBeFalse();
        (await Validator.IsActiveAsync(Guid.CreateVersion7(), _userId, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Active_check_is_cached()
    {
        _store.Sessions[_sessionId] = new SessionSnapshot(_userId, true, _key);

        await Validator.IsActiveAsync(_sessionId, _userId, Ct);
        await Validator.IsActiveAsync(_sessionId, _userId, Ct);

        _store.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Refresh_confirmation_reaches_the_store_once_per_binding_and_ignores_malformed_bindings()
    {
        var binding = RefreshTokens.Binding(RefreshTokens.Hash(RefreshTokens.Generate()));
        var next = RefreshTokens.Binding(RefreshTokens.Hash(RefreshTokens.Generate()));

        await Validator.ConfirmRefreshAsync(_sessionId, binding, Ct);
        await Validator.ConfirmRefreshAsync(_sessionId, binding, Ct);
        await Validator.ConfirmRefreshAsync(_sessionId, next, Ct);
        await Validator.ConfirmRefreshAsync(_sessionId, "not-a-binding", Ct);
        await Validator.ConfirmRefreshAsync(_sessionId, binding + "0", Ct);

        _store.Confirmations.ShouldBe([(_sessionId, binding), (_sessionId, next)]);
    }

    private sealed class FakeSnapshotStore : ISessionSnapshotStore
    {
        public Dictionary<Guid, SessionSnapshot> Sessions { get; } = [];
        public List<(Guid SessionId, string Binding)> Confirmations { get; } = [];
        public int Calls { get; private set; }

        public Task<SessionSnapshot?> FindAsync(Guid sessionId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Sessions.GetValueOrDefault(sessionId));
        }

        public Task ConfirmRefreshAsync(Guid sessionId, string binding, CancellationToken ct)
        {
            Confirmations.Add((sessionId, binding));
            return Task.CompletedTask;
        }
    }
}
