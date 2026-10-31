using Shouldly;
using SkyMessage.Application.Common.Persistence;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Application.Messaging.Services;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.UnitTests.Messaging;

public sealed class PageWindowTests
{
    [Theory]
    // (available before, available after incl. anchor, limit) → (take before, take after)
    [InlineData(100, 100, 50, 25, 25)]
    [InlineData(3, 100, 50, 3, 47)]
    [InlineData(100, 2, 50, 48, 2)]
    [InlineData(0, 0, 50, 0, 0)]
    [InlineData(10, 10, 50, 10, 10)]
    [InlineData(100, 100, 1, 0, 1)]
    [InlineData(100, 100, 7, 3, 4)]
    public void Balance(int before, int after, int limit, int expectedBefore, int expectedAfter) =>
        PageWindow.Balance(before, after, limit).ShouldBe((expectedBefore, expectedAfter));

    [Fact]
    public void Balance_never_exceeds_the_limit_or_availability()
    {
        for (var before = 0; before <= 12; before++)
        for (var after = 0; after <= 12; after++)
        for (var limit = 1; limit <= 10; limit++)
        {
            var (b, a) = PageWindow.Balance(before, after, limit);
            (b + a).ShouldBeLessThanOrEqualTo(limit);
            b.ShouldBeLessThanOrEqualTo(before);
            a.ShouldBeLessThanOrEqualTo(after);
            // Nothing is left out while there is room.
            (b + a).ShouldBe(Math.Min(limit, before + after));
        }
    }

    [Theory]
    [InlineData(null, 50)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(20, 20)]
    [InlineData(1000, 100)]
    public void Limits_are_clamped(int? requested, int expected) =>
        PageWindow.ClampLimit(requested, Limits.MessagesPageDefault, Limits.MessagesPageMax).ShouldBe(expected);
}

public sealed class LikePatternTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("100%", "100\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("%_\\", "\\%\\_\\\\")]
    public void Escapes_wildcards(string input, string expected) => LikePattern.Escape(input).ShouldBe(expected);

    [Fact]
    public void Contains_wraps_the_escaped_value() => LikePattern.Contains("5%").ShouldBe("%5\\%%");
}

public sealed class SearchQueryTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" a ", null)]
    [InlineData("  ab  ", "ab")]
    [InlineData("hello world", "hello world")]
    [InlineData("bad\0", null)]
    public void Normalize(string? query, string? expected) => SearchQuery.Normalize(query).ShouldBe(expected);

    [Theory]
    [InlineData("@alice", "alice")]
    [InlineData("@a", null)]
    [InlineData("@@bob", "bob")]
    [InlineData("Bob Smith", "Bob Smith")]
    public void Normalize_for_users_strips_at(string query, string? expected) => SearchQuery.NormalizeForUsers(query).ShouldBe(expected);

    [Fact]
    public void Overlong_queries_are_rejected() => SearchQuery.Normalize(new string('a', 257)).ShouldBeNull();
}

public sealed class ReactionRulesTests
{
    [Theory]
    [InlineData("👍")]
    [InlineData("❤️")]
    [InlineData("👨‍👩‍👧‍👦")]
    [InlineData("1️⃣")]
    [InlineData("🏳️‍🌈")]
    [InlineData("©")]
    public void Emoji_are_valid(string emoji) => ReactionRules.IsValid(emoji).ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("lol")]
    [InlineData("<3")]
    [InlineData("👍 👍")]
    [InlineData("👍\n")]
    [InlineData("a👍")]
    public void Non_emoji_are_rejected(string? emoji) => ReactionRules.IsValid(emoji).ShouldBeFalse();

    [Fact]
    public void Unpaired_surrogates_are_rejected() => ReactionRules.IsValid(new string('\uD83D', 1)).ShouldBeFalse();

    [Fact]
    public void Length_is_capped()
    {
        ReactionRules.IsValid(string.Concat(Enumerable.Repeat("👍", Limits.ReactionMaxLength / 2))).ShouldBeTrue();
        ReactionRules.IsValid(string.Concat(Enumerable.Repeat("👍", Limits.ReactionMaxLength / 2 + 1))).ShouldBeFalse();
    }
}

public sealed class MuteRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Durations()
    {
        MuteRules.MutedUntil(Now, 0).ShouldBeNull();
        MuteRules.MutedUntil(Now, -1).ShouldBe(MuteRules.Forever);
        MuteRules.MutedUntil(Now, 3600).ShouldBe(Now.AddHours(1));
        MuteRules.MutedUntil(Now, int.MaxValue)!.Value.ShouldBeLessThan(MuteRules.Forever);
        Should.Throw<ArgumentOutOfRangeException>(() => MuteRules.MutedUntil(Now, -2));
        MuteRules.IsValidDuration(-1).ShouldBeTrue();
        MuteRules.IsValidDuration(-2).ShouldBeFalse();
    }

    [Fact]
    public void Muted_state()
    {
        MuteRules.IsMuted(null, Now).ShouldBeFalse();
        MuteRules.IsMuted(Now.AddSeconds(-1), Now).ShouldBeFalse();
        MuteRules.IsMuted(Now.AddSeconds(1), Now).ShouldBeTrue();
        MuteRules.IsMuted(MuteRules.Forever, Now).ShouldBeTrue();
    }
}

public sealed class InviteTokenTests
{
    [Theory]
    [InlineData("AbCdEfGhIjKlMnOpQrStUv", true)]
    [InlineData("a-b_c", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("../../etc/passwd", false)]
    [InlineData("has space", false)]
    [InlineData("плохой", false)]
    public void Token_format(string? token, bool expected) => ChatMemberService.IsWellFormedToken(token).ShouldBe(expected);

    [Fact]
    public void Overlong_tokens_are_rejected() => ChatMemberService.IsWellFormedToken(new string('a', 65)).ShouldBeFalse();
}

public sealed class ChangesCursorRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Cursor_round_trips_exactly()
    {
        var cursor = new SyncCursor(new DateTimeOffset(2026, 10, 30, 12, 34, 56, TimeSpan.Zero).AddTicks(1230), Guid.CreateVersion7());
        SyncCursor.TryDecode(cursor.Encode(), out var decoded).ShouldBeTrue();
        decoded.ShouldBe(cursor);
        decoded.UpdatedAt.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData(":0000")]
    [InlineData("123:not-a-guid")]
    [InlineData("-5:00000000000000000000000000000000")]
    [InlineData("99999999999999999999:00000000000000000000000000000000")]
    public void Invalid_cursors_are_rejected(string? value) => SyncCursor.TryDecode(value, out _).ShouldBeFalse();

    [Fact]
    public void For_now_lags_behind()
    {
        var cursor = SyncCursor.ForNow(Now);
        cursor.UpdatedAt.ShouldBe(Now - SyncCursor.Lag);
        cursor.Id.ShouldBe(Guid.Empty);
    }

    [Fact]
    public void Max_orders_by_time_then_id()
    {
        var low = new Guid("00000000-0000-0000-0000-000000000001");
        var high = new Guid("ffffffff-0000-0000-0000-000000000000");
        var a = new SyncCursor(Now, low);
        var b = new SyncCursor(Now, high);
        SyncCursor.Max(a, b).ShouldBe(b);
        SyncCursor.Max(b, a).ShouldBe(b);
        var later = new SyncCursor(Now.AddTicks(10), low);
        SyncCursor.Max(b, later).ShouldBe(later);
    }

    [Fact]
    public void Guid_order_matches_postgres_uuid_order()
    {
        // PostgreSQL compares uuids bytewise in their textual (RFC 4122) order; the tie-break on Id relies on
        // Guid.CompareTo agreeing with it.
        var ids = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToList();
        var byCompareTo = ids.OrderBy(g => g).ToList();
        var byText = ids.OrderBy(g => g.ToString("N"), StringComparer.Ordinal).ToList();
        byCompareTo.ShouldBe(byText);
    }

    [Fact]
    public void Next_cursor_continues_after_the_last_item_when_there_is_more()
    {
        var requested = new SyncCursor(Now.AddMinutes(-30), Guid.Empty);
        var lastAt = Now.AddMinutes(-20);
        var lastId = Guid.CreateVersion7();
        ChangesCursorRules.Next(requested, Now, hasMore: true, lastAt, lastId).ShouldBe(new SyncCursor(lastAt, lastId));
    }

    [Fact]
    public void Next_cursor_when_caught_up_is_now_minus_lag_but_never_moves_backwards()
    {
        var old = new SyncCursor(Now.AddHours(-1), Guid.Empty);
        ChangesCursorRules.Next(old, Now, hasMore: false, Now.AddMinutes(-5), Guid.NewGuid()).ShouldBe(SyncCursor.ForNow(Now));

        var future = new SyncCursor(Now.AddMinutes(5), Guid.NewGuid());
        ChangesCursorRules.Next(future, Now, hasMore: false, null, null).ShouldBe(future);

        ChangesCursorRules.Next(old, Now, hasMore: true, null, null).ShouldBe(SyncCursor.ForNow(Now));
    }

    [Fact]
    public void Is_after_is_strict_on_the_tuple()
    {
        var id = new Guid("00000000-0000-0000-0000-000000000005");
        var cursor = new SyncCursor(Now, id);
        ChangesCursorRules.IsAfter(Now, id, cursor).ShouldBeFalse();
        ChangesCursorRules.IsAfter(Now, new Guid("00000000-0000-0000-0000-000000000006"), cursor).ShouldBeTrue();
        ChangesCursorRules.IsAfter(Now, new Guid("00000000-0000-0000-0000-000000000004"), cursor).ShouldBeFalse();
        ChangesCursorRules.IsAfter(Now.AddTicks(10), Guid.Empty, cursor).ShouldBeTrue();
        ChangesCursorRules.IsAfter(Now.AddTicks(-10), Guid.Empty, cursor).ShouldBeFalse();
    }
}
