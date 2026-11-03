using Shouldly;
using SkyMessage.Application.Common.Exceptions;
using SkyMessage.Application.Messaging.Rules;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.UnitTests.Messaging;

/// <summary>The folder rules every app and the server share (Contracts.ChatFolderRules) and what a saved folder may hold.</summary>
public sealed class ChatFolderRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static ChatSummaryDto Chat(ChatType type = ChatType.Group, int unread = 0, int mentions = 0, bool archived = false,
        DateTimeOffset? mutedUntil = null) => new()
    {
        Id = Guid.CreateVersion7(),
        Type = type,
        UnreadCount = unread,
        UnreadMentionCount = mentions,
        IsArchived = archived,
        MutedUntil = mutedUntil,
    };

    [Fact]
    public void Types_take_in_private_chats_by_the_contact_list_and_groups_by_type()
    {
        var contacts = new ChatFolderDto { IncludeContacts = true };
        var strangers = new ChatFolderDto { IncludeNonContacts = true };
        var groups = new ChatFolderDto { IncludeGroups = true };
        var privateChat = Chat(ChatType.Private);
        var group = Chat(ChatType.Group);

        ChatFolderRules.Contains(contacts, privateChat, peerIsContact: true, Now).ShouldBeTrue();
        ChatFolderRules.Contains(contacts, privateChat, peerIsContact: false, Now).ShouldBeFalse();
        ChatFolderRules.Contains(strangers, privateChat, peerIsContact: false, Now).ShouldBeTrue();
        ChatFolderRules.Contains(strangers, privateChat, peerIsContact: true, Now).ShouldBeFalse();
        ChatFolderRules.Contains(groups, group, false, Now).ShouldBeTrue();
        ChatFolderRules.Contains(groups, privateChat, true, Now).ShouldBeFalse();
    }

    [Fact]
    public void Saved_messages_join_a_folder_only_by_id_and_channels_have_no_chats_yet()
    {
        var saved = Chat(ChatType.Saved);
        var everything = new ChatFolderDto { IncludeContacts = true, IncludeNonContacts = true, IncludeGroups = true, IncludeChannels = true };

        ChatFolderRules.Contains(everything, saved, peerIsContact: true, Now).ShouldBeFalse();
        ChatFolderRules.Contains(everything with { IncludedChatIds = [saved.Id] }, saved, true, Now).ShouldBeTrue();
        ChatFolderRules.Contains(new ChatFolderDto { IncludeChannels = true }, Chat(ChatType.Group), false, Now).ShouldBeFalse();
    }

    [Fact]
    public void Chats_taken_in_by_id_ignore_their_type_and_excluded_ones_never_show()
    {
        var chat = Chat(ChatType.Private);
        var folder = new ChatFolderDto { IncludeGroups = true, IncludedChatIds = [chat.Id] };

        ChatFolderRules.Contains(folder, chat, peerIsContact: false, Now).ShouldBeTrue();
        ChatFolderRules.Contains(folder with { ExcludedChatIds = [chat.Id] }, chat, false, Now).ShouldBeFalse();

        var group = Chat(ChatType.Group);
        ChatFolderRules.Contains(new ChatFolderDto { IncludeGroups = true, ExcludedChatIds = [group.Id] }, group, false, Now).ShouldBeFalse();
    }

    [Fact]
    public void Exclusions_filter_every_chat_of_the_folder_including_the_ones_taken_in_by_id()
    {
        var muted = Chat(unread: 3, mutedUntil: Now.AddHours(1));
        var mutedEarlier = Chat(unread: 3, mutedUntil: Now.AddHours(-1));
        var read = Chat();
        var mentioned = Chat(mentions: 1);
        var archived = Chat(unread: 2, archived: true);
        var all = new[] { muted, mutedEarlier, read, mentioned, archived };
        var byId = new ChatFolderDto { IncludedChatIds = all.Select(c => c.Id).ToList() };

        all.Where(c => ChatFolderRules.Contains(byId with { ExcludeMuted = true }, c, false, Now)).ShouldBe([mutedEarlier, read, mentioned, archived]);
        all.Where(c => ChatFolderRules.Contains(byId with { ExcludeRead = true }, c, false, Now)).ShouldBe([muted, mutedEarlier, mentioned, archived]);
        all.Where(c => ChatFolderRules.Contains(byId with { ExcludeArchived = true }, c, false, Now)).ShouldBe([muted, mutedEarlier, read, mentioned]);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("users", true)]
    [InlineData("message-circle", true)]
    [InlineData("🏠", true)]
    [InlineData("👨‍👩‍👧", true)]
    [InlineData("❤️", true)]
    [InlineData("Users", false)]
    [InlineData("work stuff", false)]
    [InlineData("a🏠", false)]
    [InlineData("🏠 ", false)]
    [InlineData("1️⃣", false)]
    [InlineData("<b>", false)]
    public void Icons_are_one_emoji_or_an_icon_key(string? icon, bool valid)
    {
        ChatFolderRules.IsValidIcon(icon).ShouldBe(valid);
    }

    [Fact]
    public void Icons_with_broken_surrogates_or_too_long_are_refused()
    {
        ChatFolderRules.IsValidIcon("\ud83c").ShouldBeFalse();
        ChatFolderRules.IsValidIcon(new string('a', Limits.ChatFolderIconMaxLength + 1)).ShouldBeFalse();
        ChatFolderRules.IsValidIcon(string.Concat(Enumerable.Repeat("🏠", 17))).ShouldBeFalse();
    }

    private static SaveChatFolderRequest Valid => new() { Name = "  Семья ", IncludeContacts = true };

    [Fact]
    public void A_valid_folder_is_trimmed_and_keeps_its_lists()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var draft = ChatFolderInput.Normalize(Valid with { Icon = "", IncludedChatIds = [a], ExcludedChatIds = [b] });

        draft.Name.ShouldBe("Семья");
        draft.Icon.ShouldBeNull();
        draft.IncludedChatIds.ShouldBe([a]);
        draft.ExcludedChatIds.ShouldBe([b]);
        ChatFolderInput.Errors(Valid).ShouldBeEmpty();
    }

    [Fact]
    public void A_folder_must_have_a_name_and_take_in_something()
    {
        Fields(Valid with { Name = "   " }).ShouldBe(["name"]);
        Fields(Valid with { Name = new string('x', Limits.ChatFolderNameMaxLength + 1) }).ShouldBe(["name"]);
        Fields(Valid with { IncludeContacts = false }).ShouldBe(["includedChatIds"]);
        Fields(Valid with { IncludeContacts = false, IncludedChatIds = [Guid.CreateVersion7()] }).ShouldBeEmpty();
        Fields(Valid with { IncludeContacts = false, IncludeChannels = true }).ShouldBeEmpty();
        Fields(Valid with { Icon = "Work" }).ShouldBe(["icon"]);
    }

    [Fact]
    public void Chat_lists_are_distinct_bounded_and_do_not_overlap()
    {
        var a = Guid.CreateVersion7();
        Fields(Valid with { IncludedChatIds = [a, a] }).ShouldBe(["includedChatIds"]);
        Fields(Valid with { ExcludedChatIds = [Guid.Empty] }).ShouldBe(["excludedChatIds"]);
        Fields(Valid with { IncludedChatIds = [a], ExcludedChatIds = [a] }).ShouldBe(["excludedChatIds"]);
        var many = Enumerable.Range(0, Limits.MaxChatFolderChats + 1).Select(_ => Guid.CreateVersion7()).ToList();
        Fields(Valid with { ExcludedChatIds = many }).ShouldBe(["excludedChatIds"]);

        var error = Should.Throw<BadRequestException>(() => ChatFolderInput.Normalize(Valid with { Name = "", IncludeContacts = false }));
        error.Code.ShouldBe(ErrorCodes.Validation);
        error.Errors!.Keys.ShouldBe(["name", "includedChatIds"], ignoreOrder: true);
    }

    private static List<string> Fields(SaveChatFolderRequest request) => ChatFolderInput.Errors(request).Select(e => e.Field).Distinct().ToList();
}
