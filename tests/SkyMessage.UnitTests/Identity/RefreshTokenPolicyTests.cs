using Shouldly;
using SkyMessage.Application.Identity;
using Xunit;

namespace SkyMessage.UnitTests.Identity;

public sealed class RefreshTokenPolicyTests
{
    private const string Current = "current-hash";
    private const string Previous = "previous-hash";
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static RefreshTokenState State(
        DateTimeOffset? rotatedAt = null,
        DateTimeOffset? revokedAt = null,
        DateTimeOffset? expiresAt = null,
        string? previous = Previous,
        DateTimeOffset? confirmedAt = null) =>
        new(Current, previous, rotatedAt, expiresAt ?? Now.AddDays(180), revokedAt, confirmedAt);

    [Fact]
    public void Current_token_rotates()
    {
        RefreshTokenPolicy.Decide(Current, State(rotatedAt: Now.AddDays(-1)), Now).ShouldBe(RefreshDecision.Rotate);
        RefreshTokenPolicy.Decide(Current, State(previous: null), Now).ShouldBe(RefreshDecision.Rotate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(120)]
    [InlineData(3600)]
    [InlineData(30 * 24 * 3600)]
    public void Previous_token_is_a_lost_response_retry_while_its_successor_is_unconfirmed(int secondsSinceRotation)
    {
        var state = State(rotatedAt: Now.AddSeconds(-secondsSinceRotation));
        RefreshTokenPolicy.Decide(Previous, state, Now).ShouldBe(RefreshDecision.Retry);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(120)]
    public void Previous_token_within_two_minutes_of_a_confirmed_rotation_is_a_retry(int secondsSinceRotation)
    {
        var state = State(rotatedAt: Now.AddSeconds(-secondsSinceRotation), confirmedAt: Now);
        RefreshTokenPolicy.Decide(Previous, state, Now).ShouldBe(RefreshDecision.Retry);
    }

    [Theory]
    [InlineData(121)]
    [InlineData(3600)]
    public void Previous_token_after_a_confirmed_rotation_and_the_grace_window_is_theft(int secondsSinceRotation)
    {
        var state = State(rotatedAt: Now.AddSeconds(-secondsSinceRotation), confirmedAt: Now.AddSeconds(-secondsSinceRotation + 1));
        RefreshTokenPolicy.Decide(Previous, state, Now).ShouldBe(RefreshDecision.Theft);
    }

    [Fact]
    public void Previous_token_of_a_confirmed_rotation_without_a_rotation_time_is_theft()
    {
        RefreshTokenPolicy.Decide(Previous, State(rotatedAt: null, confirmedAt: Now), Now).ShouldBe(RefreshDecision.Theft);
    }

    [Fact]
    public void Unknown_token_is_invalid()
    {
        RefreshTokenPolicy.Decide("something-else", State(rotatedAt: Now), Now).ShouldBe(RefreshDecision.Invalid);
    }

    [Fact]
    public void Revoked_session_is_invalid_for_any_token()
    {
        var state = State(rotatedAt: Now.AddSeconds(-10), revokedAt: Now.AddSeconds(-5));
        RefreshTokenPolicy.Decide(Current, state, Now).ShouldBe(RefreshDecision.Invalid);
        RefreshTokenPolicy.Decide(Previous, state, Now).ShouldBe(RefreshDecision.Invalid);
    }

    [Fact]
    public void Expired_session_is_invalid()
    {
        var state = State(rotatedAt: Now.AddSeconds(-10), expiresAt: Now);
        RefreshTokenPolicy.Decide(Current, state, Now).ShouldBe(RefreshDecision.Invalid);
        RefreshTokenPolicy.Decide(Previous, state, Now).ShouldBe(RefreshDecision.Invalid);
    }

    [Fact]
    public void Generated_tokens_are_random_base64url_and_hashes_are_sha256_hex()
    {
        var a = RefreshTokens.Generate();
        var b = RefreshTokens.Generate();

        a.ShouldNotBe(b);
        a.Length.ShouldBe(43);
        a.ShouldAllBe(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');

        var hash = RefreshTokens.Hash(a);
        hash.Length.ShouldBe(64);
        hash.ShouldAllBe(c => char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c));
        RefreshTokens.Hash(a).ShouldBe(hash);
        RefreshTokens.Hash(b).ShouldNotBe(hash);
    }

    [Fact]
    public void Successors_are_deterministic_per_key_session_and_token_and_look_like_generated_tokens()
    {
        var key = RefreshTokens.SuccessorKey("test-signing-key-test-signing-key");
        var session = Guid.CreateVersion7();
        var token = RefreshTokens.Generate();

        var successor = RefreshTokens.Successor(key, session, token);

        RefreshTokens.Successor(key, session, token).ShouldBe(successor);
        successor.ShouldNotBe(token);
        successor.Length.ShouldBe(43);
        successor.ShouldAllBe(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');
        RefreshTokens.Successor(key, Guid.CreateVersion7(), token).ShouldNotBe(successor);
        RefreshTokens.Successor(key, session, RefreshTokens.Generate()).ShouldNotBe(successor);
        RefreshTokens.Successor(RefreshTokens.SuccessorKey("another-signing-key-another-key"), session, token).ShouldNotBe(successor);
    }

    [Fact]
    public void Binding_is_the_hash_prefix_and_only_lowercase_hex_of_that_length_is_accepted()
    {
        var hash = RefreshTokens.Hash(RefreshTokens.Generate());

        var binding = RefreshTokens.Binding(hash);

        binding.ShouldBe(hash[..RefreshTokens.BindingLength]);
        RefreshTokens.IsBinding(binding).ShouldBeTrue();
        RefreshTokens.IsBinding("abcdef0123456789").ShouldBeTrue();
        RefreshTokens.IsBinding("ABCDEF0123456789").ShouldBeFalse();
        RefreshTokens.IsBinding(binding[..^1]).ShouldBeFalse();
        RefreshTokens.IsBinding(binding[..^1] + "%").ShouldBeFalse();
        RefreshTokens.IsBinding(null).ShouldBeFalse();
    }
}
