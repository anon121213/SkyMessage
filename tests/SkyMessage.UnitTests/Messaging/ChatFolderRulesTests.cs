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
    [InlineData("👨\u200D👩\u200D👧", true)]
    [InlineData("👩🏽\u200D💻", true)]
    [InlineData("❤️", true)]
    [InlineData("🇷🇺", true)]
    [InlineData("ℹ️", true)]
    [InlineData("©️", true)]
    [InlineData("1️⃣", true)]
    [InlineData("#️⃣", true)]
    [InlineData("*️⃣", true)]
    [InlineData("1\u20E3", true)]
    [InlineData("🏴\U000E0067\U000E0062\U000E0065\U000E006E\U000E0067\U000E007F", true)]
    [InlineData("Users", false)]
    [InlineData("work stuff", false)]
    [InlineData("a🏠", false)]
    [InlineData("🏠 ", false)]
    [InlineData("<b>", false)]
    [InlineData("1", false)]
    [InlineData("#", false)]
    [InlineData("1\uFE0F", false)]
    [InlineData("12\u20E3", false)]
    [InlineData("1😀", false)]
    [InlineData("😀\u20E3", false)]
    [InlineData("Привет", false)]
    [InlineData("😀😀", false)]
    [InlineData("\u202E😀", false)]
    [InlineData("\u202E", false)]
    [InlineData("\u200E", false)]
    [InlineData("\u200F", false)]
    [InlineData("\u202A", false)]
    [InlineData("\u2066", false)]
    [InlineData("\u2069", false)]
    [InlineData("😀\u200F", false)]
    [InlineData("\u200B", false)]
    [InlineData("\uFEFF", false)]
    [InlineData("\u0301", false)]
    [InlineData("\u3000", false)]
    [InlineData("\u2800", false)]
    [InlineData("\uE000", false)]
    public void Icons_are_one_emoji_or_an_icon_key(string? icon, bool valid)
    {
        ChatFolderRules.IsValidIcon(icon).ShouldBe(valid);
    }

    [Fact]
    public void Icons_with_broken_surrogates_or_too_long_are_refused()
    {
        ChatFolderRules.IsValidIcon("\ud83c").ShouldBeFalse();
        ChatFolderRules.IsValidIcon("\udc00").ShouldBeFalse();
        ChatFolderRules.IsValidIcon(new string('a', Limits.ChatFolderIconMaxLength + 1)).ShouldBeFalse();
        ChatFolderRules.IsValidIcon(string.Concat(Enumerable.Repeat("🏠", 16))).ShouldBeFalse();
        ChatFolderRules.IsValidIcon(string.Concat(Enumerable.Repeat("🏠", 17))).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Семья", "Семья", true)]
    [InlineData("  \t Семья \n", "Семья", true)]
    [InlineData("a\nb", "a b", true)]
    [InlineData("a\r\nb", "a b", true)]
    [InlineData("a\u2028b", "a b", true)]
    [InlineData("a\u200Bb", "a\u200Bb", true)]
    [InlineData("🏠", "🏠", true)]
    [InlineData("", "", false)]
    [InlineData("   ", "", false)]
    [InlineData("\u200B", "\u200B", false)]
    [InlineData(" \u200B\u2060 ", "\u200B\u2060", false)]
    [InlineData("\u202E", "\u202E", false)]
    [InlineData("\u2800", "\u2800", false)]
    [InlineData("a\u0007b", "a\u0007b", false)]
    [InlineData("a\0b", "a\0b", false)]
    public void Names_get_spaces_for_line_breaks_and_need_something_visible(string input, string normalized, bool valid)
    {
        var name = ChatFolderRules.NormalizeName(input);
        name.ShouldBe(normalized);
        ChatFolderRules.IsValidName(name).ShouldBe(valid);
    }

    [Fact]
    public void Names_are_at_most_the_limit_once_normalized()
    {
        ChatFolderRules.IsValidName(new string('x', Limits.ChatFolderNameMaxLength)).ShouldBeTrue();
        ChatFolderRules.IsValidName(new string('x', Limits.ChatFolderNameMaxLength + 1)).ShouldBeFalse();
        ChatFolderRules.NormalizeName(null).ShouldBe("");
        // (Broken surrogates here: theory data would not carry them through intact.)
        ChatFolderRules.IsValidName("a\ud83cb").ShouldBeFalse();
        ChatFolderRules.IsValidName("a\udc00").ShouldBeFalse();
    }

    [Fact]
    public void A_matcher_runs_exactly_the_folder_rules()
    {
        var chats = new[]
        {
            Chat(ChatType.Private), Chat(ChatType.Private, unread: 2), Chat(ChatType.Group, mentions: 1), Chat(ChatType.Group, archived: true),
            Chat(ChatType.Saved), Chat(ChatType.Group, unread: 1, mutedUntil: Now.AddDays(1)), Chat(ChatType.Group, mutedUntil: Now.AddDays(-1)),
        };
        var folders = new[]
        {
            new ChatFolderDto { IncludeContacts = true },
            new ChatFolderDto { IncludeNonContacts = true, ExcludeRead = true },
            new ChatFolderDto { IncludeGroups = true, ExcludeMuted = true, ExcludedChatIds = [chats[2].Id] },
            new ChatFolderDto { IncludedChatIds = [chats[0].Id, chats[4].Id, chats[3].Id], ExcludeArchived = true },
            new ChatFolderDto { IncludeContacts = true, IncludeNonContacts = true, IncludeGroups = true, ExcludeRead = true, ExcludeArchived = true },
        };
        foreach (var folder in folders)
        {
            var matcher = new ChatFolderMatcher(folder);
            foreach (var chat in chats)
            foreach (var contact in new[] { true, false })
                matcher.Contains(chat, contact, Now).ShouldBe(ChatFolderRules.Contains(folder, chat, contact, Now));
        }
    }

    private static SaveChatFolderRequest Valid => new() { Create = true, Name = "  Семья ", IncludeContacts = true };

    [Fact]
    public void A_valid_folder_is_trimmed_and_keeps_its_lists()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var draft = ChatFolderInput.Apply(Valid with { Name = "Се\nмья ", Icon = "", IncludedChatIds = [a], ExcludedChatIds = [b] }, null);

        draft.Name.ShouldBe("Се мья");
        draft.Icon.ShouldBeNull();
        draft.IncludeContacts.ShouldBeTrue();
        draft.IncludedChatIds.ShouldBe([a]);
        draft.ExcludedChatIds.ShouldBe([b]);
        ChatFolderInput.Errors(Valid).ShouldBeEmpty();
    }

    [Fact]
    public void A_folder_must_have_a_name_and_take_in_something()
    {
        Fields(Valid with { Name = "   " }).ShouldBe(["name"]);
        Fields(Valid with { Name = null }).ShouldBe(["name"]);
        Fields(Valid with { Name = "\u200B" }).ShouldBe(["name"]);
        Fields(Valid with { Name = new string('x', Limits.ChatFolderNameMaxLength + 1) }).ShouldBe(["name"]);
        Fields(Valid with { IncludeContacts = false }).ShouldBe(["includedChatIds"]);
        Fields(Valid with { IncludeContacts = null }).ShouldBe(["includedChatIds"]);
        Fields(Valid with { IncludeContacts = false, IncludedChatIds = [Guid.CreateVersion7()] }).ShouldBeEmpty();
        Fields(Valid with { IncludeContacts = false, IncludeChannels = true }).ShouldBeEmpty();
        Fields(Valid with { Icon = "Work" }).ShouldBe(["icon"]);
        Fields(Valid with { Icon = "\u202E😀" }).ShouldBe(["icon"]);
    }

    [Fact]
    public void An_edit_changes_only_the_fields_it_sets()
    {
        var a = Guid.CreateVersion7();
        var current = new ChatFolderDraft
        {
            Name = "Семья",
            Icon = "🏠",
            IncludeContacts = true,
            ExcludeMuted = true,
            IncludedChatIds = [a],
        };

        // Only a new name: the rules, the icon and the chats stay (an app that does not know a field never resets it).
        var renamed = ChatFolderInput.Apply(new SaveChatFolderRequest { Name = "Родные" }, current);
        renamed.ShouldBe(current with { Name = "Родные" });
        Fields(new SaveChatFolderRequest { Name = "Родные" }).ShouldBeEmpty();

        // "" clears the icon; null keeps it.
        ChatFolderInput.Apply(new SaveChatFolderRequest { Icon = "" }, current).Icon.ShouldBeNull();
        ChatFolderInput.Apply(new SaveChatFolderRequest(), current).ShouldBe(current);

        // The whole folder is checked once the edit is laid over it.
        var nothing = Should.Throw<BadRequestException>(() =>
            ChatFolderInput.Apply(new SaveChatFolderRequest { IncludeContacts = false, IncludedChatIds = [] }, current));
        nothing.Errors!.Keys.ShouldBe(["includedChatIds"]);
        var overlap = Should.Throw<BadRequestException>(() => ChatFolderInput.Apply(new SaveChatFolderRequest { ExcludedChatIds = [a] }, current));
        overlap.Errors!.Keys.ShouldBe(["excludedChatIds"]);
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

        var error = Should.Throw<BadRequestException>(() => ChatFolderInput.Apply(Valid with { Name = "", IncludeContacts = false }, null));
        error.Code.ShouldBe(ErrorCodes.Validation);
        error.Errors!.Keys.ShouldBe(["name", "includedChatIds"], ignoreOrder: true);
    }

    [Fact]
    public void A_settings_change_sets_only_its_fields()
    {
        var settings = new ChatListSettingsDto { ArchiveNewFromStrangers = true, KeepArchived = false, HideArchive = true };

        new UpdateChatListSettingsRequest { HideArchive = false }.ApplyTo(settings)
            .ShouldBe(settings with { HideArchive = false });
        new UpdateChatListSettingsRequest().ApplyTo(settings).ShouldBe(settings);
    }

    private static List<string> Fields(SaveChatFolderRequest request) => ChatFolderInput.Errors(request).Select(e => e.Field).Distinct().ToList();
}
