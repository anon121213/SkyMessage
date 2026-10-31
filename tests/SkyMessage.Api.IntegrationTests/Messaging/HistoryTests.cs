using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using SkyMessage.Application.Common.Persistence;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Messaging;

/// <summary>History pages and incremental sync through GET .../messages/changes (SPEC §6.1, §6.3).</summary>
[Collection(ApiCollection.Name)]
public sealed class HistoryTests(ApiFactory factory)
{
    private static IEnumerable<long> Seqs(long from, long to) => Enumerable.Range((int)from, (int)(to - from + 1)).Select(i => (long)i);

    private static string Cursor(MessageDto after) => new SyncCursor(after.UpdatedAt, after.Id).Encode();

    /// <summary>Same order as PostgreSQL's (timestamptz, uuid) row comparison (uuid compares like its text form).</summary>
    private static int CompareChange(MessageDto a, MessageDto b)
    {
        var byTime = a.UpdatedAt.CompareTo(b.UpdatedAt);
        return byTime != 0 ? byTime : string.CompareOrdinal(a.Id.ToString(), b.Id.ToString());
    }

    [Fact]
    public async Task Pages_walk_the_history_in_both_directions()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        await bob.SendManyAsync(chat, 120);

        var latest = await alice.PageAsync(chat);
        latest.Messages.Select(m => m.Seq).ShouldBe(Seqs(71, 120));
        latest.HasMoreBefore.ShouldBeTrue();
        latest.HasMoreAfter.ShouldBeFalse();

        var older = await alice.PageAsync(chat, beforeSeq: 71);
        older.Messages.Select(m => m.Seq).ShouldBe(Seqs(21, 70));
        older.HasMoreBefore.ShouldBeTrue();
        older.HasMoreAfter.ShouldBeTrue();

        var oldest = await alice.PageAsync(chat, beforeSeq: 21);
        oldest.Messages.Select(m => m.Seq).ShouldBe(Seqs(1, 20));
        oldest.HasMoreBefore.ShouldBeFalse();
        oldest.HasMoreAfter.ShouldBeTrue();

        var forward = await alice.PageAsync(chat, afterSeq: 0, limit: 10);
        forward.Messages.Select(m => m.Seq).ShouldBe(Seqs(1, 10));
        forward.HasMoreBefore.ShouldBeFalse();
        forward.HasMoreAfter.ShouldBeTrue();

        var tail = await alice.PageAsync(chat, afterSeq: 100, limit: 50);
        tail.Messages.Select(m => m.Seq).ShouldBe(Seqs(101, 120));
        tail.HasMoreBefore.ShouldBeTrue();
        tail.HasMoreAfter.ShouldBeFalse();

        var exactEnd = await alice.PageAsync(chat, afterSeq: 110, limit: 10);
        exactEnd.Messages.Select(m => m.Seq).ShouldBe(Seqs(111, 120));
        exactEnd.HasMoreAfter.ShouldBeFalse();

        var around = await alice.PageAsync(chat, aroundSeq: 60, limit: 10);
        around.Messages.Select(m => m.Seq).ShouldBe(Seqs(55, 64)); // the anchor belongs to the newer half
        around.HasMoreBefore.ShouldBeTrue();
        around.HasMoreAfter.ShouldBeTrue();

        var aroundEnd = await alice.PageAsync(chat, aroundSeq: 118, limit: 10);
        aroundEnd.Messages.Select(m => m.Seq).ShouldBe(Seqs(111, 120));
        aroundEnd.HasMoreBefore.ShouldBeTrue();
        aroundEnd.HasMoreAfter.ShouldBeFalse();

        var aroundStart = await alice.PageAsync(chat, aroundSeq: 2, limit: 10);
        aroundStart.Messages.Select(m => m.Seq).ShouldBe(Seqs(1, 10));
        aroundStart.HasMoreBefore.ShouldBeFalse();
        aroundStart.HasMoreAfter.ShouldBeTrue();

        (await alice.PageAsync(chat, limit: 1_000)).Messages.Count.ShouldBe(Limits.MessagesPageMax);
        (await alice.PageAsync(chat, limit: 0)).Messages.Select(m => m.Seq).ShouldBe([120L]);
        (await alice.PageAsync(chat, beforeSeq: 1)).Messages.ShouldBeEmpty();
        (await alice.PageAsync(chat, afterSeq: 120)).Messages.ShouldBeEmpty();
        var none = await alice.PageAsync(chat, afterSeq: 120);
        none.HasMoreBefore.ShouldBeTrue();
        none.HasMoreAfter.ShouldBeFalse();
    }

    [Fact]
    public async Task Pages_skip_tombstones_hidden_and_cleared_messages()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var m = await bob.SendManyAsync(chat, 20);

        await bob.DeleteMessagesAsync(chat, forEveryone: true, m[19].Id, m[17].Id); // seq 20, 18
        await alice.DeleteMessagesAsync(chat, forEveryone: false, m[18].Id);       // seq 19 for Alice only

        var latest = await alice.PageAsync(chat, limit: 5);
        latest.Messages.Select(x => x.Seq).ShouldBe([13L, 14L, 15L, 16L, 17L]);
        latest.HasMoreAfter.ShouldBeFalse();
        latest.HasMoreBefore.ShouldBeTrue();
        (await bob.PageAsync(chat, limit: 5)).Messages.Select(x => x.Seq).ShouldBe([14L, 15L, 16L, 17L, 19L]);

        // A window whose newer side holds only invisible rows has nothing more after it.
        var window = await alice.PageAsync(chat, afterSeq: 16, limit: 5);
        window.Messages.Select(x => x.Seq).ShouldBe([17L]);
        window.HasMoreAfter.ShouldBeFalse();
        (await alice.PageAsync(chat, beforeSeq: 18, limit: 1)).HasMoreAfter.ShouldBeFalse();

        await alice.ClearHistoryAsync(chat);
        var fresh = await bob.SendAsync(chat, "after clearing");
        var page = await alice.PageAsync(chat);
        page.Messages.Select(x => x.Id).ShouldBe([fresh.Id]);
        page.HasMoreBefore.ShouldBeFalse();
        (await alice.PageAsync(chat, aroundSeq: 5)).Messages.Select(x => x.Id).ShouldBe([fresh.Id]);
        await (await alice.GetRawAsync(ApiRoutes.Messages.ByIdPath(chat, m[0].Id))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);
        (await bob.GetMessageAsync(chat, m[0].Id)).Text.ShouldBe("msg 1");
    }

    [Fact]
    public async Task Pages_carry_people_my_reactions_and_a_sync_cursor()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var carol = await factory.NewUserAsync("Carol");
        var chat = await alice.NewGroupAsync(bob, carol);
        var fromBob = await bob.SendAsync(chat, "hi");
        var fromAlice = await alice.SendAsync(chat, "hello @someone", entities:
            [new MessageEntityDto { Type = MessageEntityType.Mention, Offset = 6, Length = 8, UserId = carol.Id }]);
        await alice.ReactAsync(chat, fromBob.Id, "👍");
        await bob.ReactAsync(chat, fromBob.Id, "❤️");
        await bob.ReactAsync(chat, fromAlice.Id, "😂");

        var before = TestKit.Now();
        var page = await alice.PageAsync(chat);
        var after = TestKit.Now();

        page.Messages.Count.ShouldBe(3); // ChatCreated + 2
        page.Users.Select(u => u.Id).ShouldBe([alice.Id, bob.Id, carol.Id], ignoreOrder: true);
        page.MyReactions.Count.ShouldBe(1);
        page.MyReactions[fromBob.Id].ShouldBe("👍");
        var bobs = await bob.PageAsync(chat);
        bobs.MyReactions.Count.ShouldBe(2);
        bobs.MyReactions[fromBob.Id].ShouldBe("❤️");
        bobs.MyReactions[fromAlice.Id].ShouldBe("😂");
        (await carol.PageAsync(chat)).MyReactions.ShouldBeEmpty();

        // The cursor lags "now" by SyncCursor.Lag so rows committed late are not skipped.
        SyncCursor.TryDecode(page.SyncCursor, out var cursor).ShouldBeTrue();
        cursor.Id.ShouldBe(Guid.Empty);
        cursor.UpdatedAt.ShouldBeInRange(before - SyncCursor.Lag, after - SyncCursor.Lag);

        // Everything changed after the page was read shows up when syncing from its cursor.
        var edited = await alice.EditAsync(chat, fromAlice.Id, "hello again");
        var changes = await alice.ChangesAsync(chat, page.SyncCursor);
        changes.Messages.ShouldContain(x => x.Id == fromAlice.Id && x.Text == "hello again" && x.EditedAt == edited.EditedAt);
    }

    [Fact]
    public async Task Pages_and_changes_require_membership()
    {
        var alice = await factory.NewUserAsync("Alice");
        var outsider = await factory.NewUserAsync("Outsider");
        var message = await alice.SendAsync(alice.SavedChatId, "private note");
        var cursor = new SyncCursor(DateTimeOffset.UnixEpoch, Guid.Empty).Encode();

        await (await outsider.GetRawAsync(ApiRoutes.Messages.PagePath(alice.SavedChatId))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await (await outsider.GetRawAsync(ApiRoutes.Messages.ChangesPath(alice.SavedChatId, cursor))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await (await outsider.GetRawAsync(ApiRoutes.Messages.ByIdPath(alice.SavedChatId, message.Id))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.ChatNotFound);
        await (await outsider.GetRawAsync(ApiRoutes.Messages.ByIdPath(outsider.SavedChatId, message.Id))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MessageNotFound);
    }

    [Fact]
    public async Task Changes_report_edits_reactions_tombstones_and_new_messages_after_the_cursor()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var m = await bob.SendManyAsync(chat, 4);
        var cursor = Cursor(m[3]);

        var nothing = await alice.ChangesAsync(chat, cursor);
        nothing.Messages.ShouldBeEmpty();
        nothing.HiddenMessageIds.ShouldBeEmpty();
        nothing.HasMore.ShouldBeFalse();
        nothing.NextCursor.ShouldBe(cursor); // never moves backwards to "now - lag"
        nothing.ClearedUpToSeq.ShouldBe(0);

        var edited = await bob.EditAsync(chat, m[0].Id, "msg 1 (edited)");
        edited.EditedAt.ShouldNotBeNull();
        edited.UpdatedAt.ShouldBeGreaterThan(m[3].UpdatedAt);
        var reacted = await alice.ReactAsync(chat, m[1].Id, "🔥");
        await bob.DeleteMessagesAsync(chat, forEveryone: true, m[2].Id);
        var added = await alice.SendAsync(chat, "new one");

        var changes = await alice.ChangesAsync(chat, cursor);
        changes.HasMore.ShouldBeFalse();
        changes.Messages.Select(x => x.Id).ShouldBe([m[0].Id, m[1].Id, m[2].Id, added.Id]);
        changes.Messages.ShouldBeInOrder(SortDirection.Ascending, Comparer<MessageDto>.Create(CompareChange));
        changes.Messages[0].Text.ShouldBe("msg 1 (edited)");
        changes.Messages[0].EditedAt.ShouldBe(edited.EditedAt);
        changes.Messages[1].Reactions.Single().Emoji.ShouldBe("🔥");
        changes.Messages[1].UpdatedAt.ShouldBe(reacted.UpdatedAt);
        var tombstone = changes.Messages[2];
        tombstone.IsDeleted.ShouldBeTrue();
        tombstone.Text.ShouldBeNull();
        tombstone.Entities.ShouldBeNull();
        tombstone.Attachments.ShouldBeEmpty();
        tombstone.Seq.ShouldBe(3);
        changes.Messages[3].Text.ShouldBe("new one");

        // Strictly after the cursor tuple: the last item itself is not repeated.
        var fromLast = await alice.ChangesAsync(chat, Cursor(changes.Messages[^1]));
        fromLast.Messages.ShouldBeEmpty();
        var fromSecond = await alice.ChangesAsync(chat, Cursor(changes.Messages[1]));
        fromSecond.Messages.Select(x => x.Id).ShouldBe([m[2].Id, added.Id]);

        // Tombstones are in /changes but not in pages.
        (await alice.PageAsync(chat)).Messages.ShouldNotContain(x => x.Id == m[2].Id);
    }

    [Fact]
    public async Task Changes_report_messages_hidden_for_me_and_my_clear_history_mark()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var chat = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var m = await bob.SendManyAsync(chat, 3);
        var cursor = Cursor(m[2]);
        var aliceTablet = await alice.SecondDeviceAsync();

        await alice.DeleteMessagesAsync(chat, forEveryone: false, m[0].Id);
        // Hidden rows are not changes of the message: they are reported by id, to my devices only.
        var mine = await aliceTablet.ChangesAsync(chat, cursor);
        mine.HiddenMessageIds.ShouldBe([m[0].Id]);
        mine.Messages.ShouldBeEmpty();
        (await bob.ChangesAsync(chat, cursor)).HiddenMessageIds.ShouldBeEmpty();

        // A hidden message that changes later stays out of my changes.
        await bob.EditAsync(chat, m[0].Id, "edited after Alice hid it");
        (await alice.ChangesAsync(chat, cursor)).Messages.ShouldBeEmpty();
        (await bob.ChangesAsync(chat, cursor)).Messages.Select(x => x.Id).ShouldBe([m[0].Id]);

        // Hidden before the cursor time → not reported again.
        var later = new SyncCursor(TestKit.Now().AddMinutes(1), Guid.Empty).Encode();
        (await alice.ChangesAsync(chat, later)).HiddenMessageIds.ShouldBeEmpty();

        // Clear history: the watermark comes with every response and cleared rows never come back.
        await alice.ClearHistoryAsync(chat);
        await bob.EditAsync(chat, m[1].Id, "edited after the clear");
        var afterClear = await alice.ChangesAsync(chat, cursor);
        afterClear.ClearedUpToSeq.ShouldBe(3);
        afterClear.Messages.ShouldBeEmpty();
        var next = await bob.SendAsync(chat, "visible again");
        (await alice.ChangesAsync(chat, cursor)).Messages.Select(x => x.Id).ShouldBe([next.Id]);
        (await bob.ChangesAsync(chat, cursor)).ClearedUpToSeq.ShouldBe(0);
    }

    [Fact]
    public async Task Changes_from_an_old_cursor_hand_out_a_lagging_now_cursor()
    {
        var alice = await factory.NewUserAsync("Alice");
        var sent = await alice.SendAsync(alice.SavedChatId, "note");
        var epoch = new SyncCursor(DateTimeOffset.UnixEpoch, Guid.Empty).Encode();

        var before = TestKit.Now();
        var changes = await alice.ChangesAsync(alice.SavedChatId, epoch);
        var after = TestKit.Now();

        changes.Messages.Select(x => x.Id).ShouldBe([sent.Id]);
        changes.HasMore.ShouldBeFalse();
        SyncCursor.TryDecode(changes.NextCursor, out var next).ShouldBeTrue();
        next.Id.ShouldBe(Guid.Empty);
        next.UpdatedAt.ShouldBeInRange(before - SyncCursor.Lag, after - SyncCursor.Lag);

        // Cursors from the far past or future are fine too.
        (await alice.ChangesAsync(alice.SavedChatId, new SyncCursor(DateTimeOffset.MinValue, Guid.Empty).Encode())).Messages.Count.ShouldBe(1);
        var future = new SyncCursor(new DateTimeOffset(9000, 1, 1, 0, 0, 0, TimeSpan.Zero), Guid.Empty).Encode();
        var none = await alice.ChangesAsync(alice.SavedChatId, future);
        none.Messages.ShouldBeEmpty();
        none.NextCursor.ShouldBe(future);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("123")]
    [InlineData("123:")]
    [InlineData(":0123456789abcdef0123456789abcdef")]
    [InlineData("-5:0123456789abcdef0123456789abcdef")]
    [InlineData("12x:0123456789abcdef0123456789abcdef")]
    [InlineData("123:not-a-guid")]
    [InlineData("99999999999999999999:0123456789abcdef0123456789abcdef")]
    [InlineData("3155378976000000000:0123456789abcdef0123456789abcdef")] // > DateTimeOffset.MaxValue ticks
    public async Task Invalid_cursors_are_validation_errors(string cursor)
    {
        var alice = await factory.NewUserAsync("Alice");
        await (await alice.GetRawAsync(ApiRoutes.Messages.ChangesPath(alice.SavedChatId, cursor))).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task Missing_cursor_is_a_validation_error()
    {
        var alice = await factory.NewUserAsync("Alice");
        await (await alice.GetRawAsync($"api/chats/{alice.SavedChatId}/messages/changes")).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task Changes_page_through_ties_on_updated_at_without_gaps_or_repeats()
    {
        var alice = await factory.NewUserAsync("Alice");
        var bob = await factory.NewUserAsync("Bob");
        var source = alice.SavedChatId;
        var target = (await alice.OpenPrivateAsync(bob.Id)).Id;
        var originals = await alice.SendManyAsync(source, Limits.MaxMessagesPerBatch, "original");

        // Every forward stamps all its copies with the same instant: 50 + 5 × 100 = 550 rows in 6 tie groups,
        // so the 500-row page boundary falls inside a group.
        var batches = new[] { 50, 100, 100, 100, 100, 100 };
        foreach (var size in batches)
        {
            var ids = originals.Take(size).Select(o => o.Id).ToList();
            var copies = await alice.ForwardAsync(target, new ForwardMessagesRequest
            {
                FromChatId = source,
                MessageIds = ids,
                ClientMessageIds = ids.Select(_ => Guid.NewGuid()).ToList(),
            });
            copies.Count.ShouldBe(size);
            copies.Select(c => c.UpdatedAt).Distinct().Count().ShouldBe(1);
        }

        var start = new SyncCursor(DateTimeOffset.UnixEpoch, Guid.Empty).Encode();
        var first = await bob.ChangesAsync(target, start);
        first.HasMore.ShouldBeTrue();
        first.Messages.Count.ShouldBe(Limits.MessageChangesMax);
        first.NextCursor.ShouldBe(Cursor(first.Messages[^1]));
        first.Messages[^1].UpdatedAt.ShouldBe(first.Messages[^2].UpdatedAt); // the boundary splits a tie group

        var second = await bob.ChangesAsync(target, first.NextCursor);
        second.HasMore.ShouldBeFalse();
        second.Messages.Count.ShouldBe(550 - Limits.MessageChangesMax);
        second.Messages[0].UpdatedAt.ShouldBe(first.Messages[^1].UpdatedAt);

        var all = first.Messages.Concat(second.Messages).ToList();
        all.Select(x => x.Id).Distinct().Count().ShouldBe(550);
        all.ShouldBeInOrder(SortDirection.Ascending, Comparer<MessageDto>.Create(CompareChange));
        var expected = await factory.WithDbAsync(db => db.Messages.Where(x => x.ChatId == target).Select(x => x.Id).ToListAsync());
        all.Select(x => x.Id).ShouldBe(expected, ignoreOrder: true);
        all.Select(x => x.Seq).Order().ShouldBe(Seqs(1, 550));
    }
}
