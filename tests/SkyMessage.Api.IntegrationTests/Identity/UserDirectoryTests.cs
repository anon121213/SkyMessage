using System.Net;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Identity.IdentityKit;

namespace SkyMessage.Api.IntegrationTests.Identity;

[Collection(ApiCollection.Name)]
public sealed class UserDirectoryTests(ApiFactory factory)
{
    // ---- Batch ----

    [Fact]
    public async Task Batch_returns_viewer_dependent_summaries_in_request_order_skipping_unknown_ids()
    {
        var viewer = await factory.NewUserAsync();
        var (a, b) = (await factory.NewUserAsync(displayName: "User A"), await factory.NewUserAsync(displayName: "User B"));
        await viewer.AddContactAsync(b, "My B");

        var result = await viewer.Client.PostAs<IReadOnlyList<UserSummaryDto>>(ApiRoutes.Users.Batch,
            new GetUsersRequest { UserIds = [b.Id, Guid.CreateVersion7(), a.Id, b.Id, viewer.Id] });

        result.Select(u => u.Id).ShouldBe([b.Id, a.Id, viewer.Id]);
        result[0].ContactName.ShouldBe("My B");
        result[0].IsContact.ShouldBeTrue();
        result[0].DisplayName.ShouldBe("User B");
        result[1].IsContact.ShouldBeFalse();
        result[1].ContactName.ShouldBeNull();
        result[1].Username.ShouldBe(a.Username);
        result[2].IsContact.ShouldBeFalse();
    }

    [Fact]
    public async Task Batch_is_limited_to_200_ids()
    {
        var viewer = await factory.NewUserAsync();
        var tooMany = Enumerable.Range(0, Limits.MaxBatchUsers + 1).Select(_ => Guid.CreateVersion7()).ToList();

        await (await viewer.Client.Post(ApiRoutes.Users.Batch, new GetUsersRequest { UserIds = tooMany }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        (await viewer.Client.PostAs<IReadOnlyList<UserSummaryDto>>(ApiRoutes.Users.Batch, new GetUsersRequest { UserIds = tooMany.Take(200).ToList() }))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task Batch_marks_deleted_accounts()
    {
        var viewer = await factory.NewUserAsync();
        var gone = await factory.NewUserAsync();
        await (await gone.Client.Post(ApiRoutes.Users.MeDelete, new DeleteAccountRequest { Password = gone.Password })).ShouldBeStatusAsync(HttpStatusCode.NoContent);

        var summary = (await viewer.Client.PostAs<IReadOnlyList<UserSummaryDto>>(ApiRoutes.Users.Batch, new GetUsersRequest { UserIds = [gone.Id] })).Single();

        summary.IsDeleted.ShouldBeTrue();
        summary.Presence.Approx.ShouldBe(LastSeenApprox.LongAgo);
    }

    // ---- Search ----

    [Fact]
    public async Task Strangers_are_found_only_by_a_username_prefix_of_at_least_three_characters()
    {
        var tag = Tag();
        var viewer = await factory.NewUserAsync();
        var stranger = await factory.NewUserAsync(tag + "Stranger", $"Display {tag}");

        (await Search(viewer, tag[..3])).ShouldContain(u => u.Id == stranger.Id);
        (await Search(viewer, tag.ToUpperInvariant())).ShouldContain(u => u.Id == stranger.Id);
        (await Search(viewer, "@" + tag)).ShouldContain(u => u.Id == stranger.Id);
        (await Search(viewer, tag[..2])).ShouldNotContain(u => u.Id == stranger.Id);
        // Not by display name and not by the middle of the username.
        (await Search(viewer, $"Display {tag}")).ShouldNotContain(u => u.Id == stranger.Id);
        (await Search(viewer, tag[1..])).ShouldNotContain(u => u.Id == stranger.Id);
        (await Search(viewer, "Stranger")).ShouldNotContain(u => u.Id == stranger.Id);
    }

    [Fact]
    public async Task People_i_know_are_found_by_display_name_contact_name_and_short_username_prefix()
    {
        var tag = Tag();
        var viewer = await factory.NewUserAsync();
        var contact = await factory.NewUserAsync(displayName: $"Grandma {tag} Ivanova");
        var renamed = await factory.NewUserAsync(displayName: "Someone");
        var groupMate = await factory.NewUserAsync(displayName: $"Cousin {tag}");
        var chatPeer = await factory.NewUserAsync(displayName: $"Uncle {tag}");
        var shortName = await factory.NewUserAsync(tag[..2] + "short" + Guid.NewGuid().ToString("N")[..6]);
        await viewer.AddContactAsync(contact);
        await viewer.AddContactAsync(renamed, $"Dad {tag}");
        await viewer.AddContactAsync(shortName);
        await viewer.CreateGroupAsync(groupMate);
        await chatPeer.CreatePrivateChatWithMessageAsync(viewer);

        var byName = await Search(viewer, tag.ToLowerInvariant());
        byName.Select(u => u.Id).ShouldBe([contact.Id, renamed.Id, groupMate.Id, chatPeer.Id], ignoreOrder: true);
        var renamedHit = byName.Single(u => u.Id == renamed.Id);
        renamedHit.ContactName.ShouldBe($"Dad {tag}");
        renamedHit.IsContact.ShouldBeTrue();

        (await Search(viewer, "ivanova")).ShouldContain(u => u.Id == contact.Id);
        (await Search(viewer, tag[..2])).ShouldContain(u => u.Id == shortName.Id);
    }

    [Fact]
    public async Task Contacts_come_first_then_the_exact_username_then_the_rest()
    {
        var tag = Tag();
        var viewer = await factory.NewUserAsync();
        var exact = await factory.NewUserAsync(tag);
        var longer = await factory.NewUserAsync(tag + "a");
        var contact = await factory.NewUserAsync(tag + "zz", "Zed");
        var known = await factory.NewUserAsync(tag + "b");
        await viewer.AddContactAsync(contact);
        await viewer.CreateGroupAsync(known);

        var results = await Search(viewer, tag);

        results.Select(u => u.Id).ShouldBe([contact.Id, exact.Id, known.Id, longer.Id]);
        results[0].IsContact.ShouldBeTrue();
    }

    [Fact]
    public async Task Search_excludes_self_deleted_users_and_users_who_blocked_me()
    {
        var tag = Tag();
        var viewer = await factory.NewUserAsync(tag + "self");
        var visible = await factory.NewUserAsync(tag + "visible");
        var blocker = await factory.NewUserAsync(tag + "blocker");
        var blockedByMe = await factory.NewUserAsync(tag + "blocked");
        var deleted = await factory.NewUserAsync(tag + "deleted");
        await blocker.BlockAsync(viewer);
        await viewer.BlockAsync(blockedByMe);
        await (await deleted.Client.Post(ApiRoutes.Users.MeDelete, new DeleteAccountRequest { Password = deleted.Password })).ShouldBeStatusAsync(HttpStatusCode.NoContent);

        var results = await Search(viewer, tag);

        results.Select(u => u.Id).ShouldBe([visible.Id, blockedByMe.Id], ignoreOrder: true);
        // A known blocker is hidden from display-name search too.
        var knownBlocker = await factory.NewUserAsync(displayName: $"Known {tag}");
        await viewer.AddContactAsync(knownBlocker);
        (await Search(viewer, $"Known {tag}")).ShouldContain(u => u.Id == knownBlocker.Id);
        await knownBlocker.BlockAsync(viewer);
        (await Search(viewer, $"Known {tag}")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_needs_two_characters_and_respects_the_limit()
    {
        var tag = Tag();
        var viewer = await factory.NewUserAsync();
        for (var i = 0; i < 4; i++)
            await factory.NewUserAsync(tag + "n" + i);

        (await Search(viewer, "a")).ShouldBeEmpty();
        (await Search(viewer, "@a")).ShouldBeEmpty();
        (await Search(viewer, "  ")).ShouldBeEmpty();
        (await Search(viewer, tag, limit: 2)).Count.ShouldBe(2);
        (await Search(viewer, tag, limit: 500)).Count.ShouldBe(4);
        (await viewer.Client.GetAs<IReadOnlyList<UserSummaryDto>>(ApiRoutes.Users.Search + "?q=" + tag)).Count.ShouldBe(4);
    }

    [Fact]
    public async Task Search_is_rate_limited_per_user()
    {
        var viewer = await factory.NewUserAsync();
        var other = await factory.NewUserAsync();
        for (var i = 0; i < 120; i++)
            await (await viewer.Client.Get(ApiRoutes.Users.SearchPath("ab"))).ShouldBeStatusAsync(HttpStatusCode.OK);

        await (await viewer.Client.Get(ApiRoutes.Users.SearchPath("ab"))).ShouldFailAsync(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        await (await other.Client.Get(ApiRoutes.Users.SearchPath("ab"))).ShouldBeStatusAsync(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("%%%")]
    [InlineData("___")]
    [InlineData("%_%")]
    [InlineData("\\%\\")]
    public async Task Like_wildcards_in_the_query_match_literally(string query)
    {
        var viewer = await factory.NewUserAsync();
        var known = await factory.NewUserAsync(displayName: "Plain Name");
        await viewer.AddContactAsync(known);
        await factory.NewUserAsync();

        (await Search(viewer, query)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_underscore_in_a_username_prefix_matches_only_an_underscore()
    {
        var tag = Tag();
        var viewer = await factory.NewUserAsync();
        var underscore = await factory.NewUserAsync(tag + "_x");
        var letter = await factory.NewUserAsync(tag + "ax");

        var results = await Search(viewer, tag + "_");

        results.Select(u => u.Id).ShouldBe([underscore.Id]);
        letter.Id.ShouldNotBe(underscore.Id);
    }

    // ---- Find by phones ----

    [Fact]
    public async Task Find_by_phone_respects_the_owners_FindByPhone_setting()
    {
        var viewer = await factory.NewUserAsync();
        var hidden = await WithPhoneAsync(TestPhones.Thai(), PrivacyLevel.Nobody);
        var open = await WithPhoneAsync(TestPhones.Thai(), PrivacyLevel.Everybody);
        var contactsOnly = await WithPhoneAsync(TestPhones.Thai(), PrivacyLevel.Contacts);
        var contactsOnlyKnowsMe = await WithPhoneAsync(TestPhones.Thai(), PrivacyLevel.Contacts);
        await contactsOnlyKnowsMe.User.AddContactAsync(viewer);

        var matches = await FindByPhones(viewer, [hidden.Phone.E164, open.Phone.E164, contactsOnly.Phone.E164, contactsOnlyKnowsMe.Phone.E164]);

        matches.Select(m => m.User.Id).ShouldBe([open.User.Id, contactsOnlyKnowsMe.User.Id], ignoreOrder: true);
        matches.Single(m => m.User.Id == open.User.Id).PhoneNumber.ShouldBe(open.Phone.E164);
    }

    [Fact]
    public async Task Thai_national_numbers_are_read_in_the_requested_regions_and_echoed_verbatim()
    {
        var viewer = await factory.NewUserAsync();
        var thai = await WithPhoneAsync(TestPhones.Thai(), PrivacyLevel.Everybody);
        var russian = await WithPhoneAsync(TestPhones.Russian(), PrivacyLevel.Everybody);
        var national = thai.Phone.National; // "08x xxx xxxx"
        var compact = national.Replace(" ", "");

        // Without a region the RU fallback cannot read a Thai national number.
        (await FindByPhones(viewer, [national])).ShouldBeEmpty();

        var viaRegion = await FindByPhones(viewer, [national, russian.Phone.National], defaultRegion: "TH");
        viaRegion.Select(m => (m.PhoneNumber, m.User.Id)).ShouldBe([(national, thai.User.Id), (russian.Phone.National, russian.User.Id)], ignoreOrder: true);

        var viaRegions = await FindByPhones(viewer, [compact, "+66 " + compact[1..]], regions: ["RU", "TH"]);
        viaRegions.Select(m => m.PhoneNumber).ShouldBe([compact, "+66 " + compact[1..]], ignoreOrder: true);
        viaRegions.ShouldAllBe(m => m.User.Id == thai.User.Id);

        // DefaultRegions takes precedence over DefaultRegion; unknown regions are ignored.
        (await FindByPhones(viewer, [national], defaultRegion: "RU", regions: ["XX", "th"])).ShouldHaveSingleItem().User.Id.ShouldBe(thai.User.Id);
    }

    [Fact]
    public async Task The_callers_own_phone_region_is_used_when_no_region_is_given()
    {
        var viewer = await factory.NewUserAsync();
        await viewer.Client.PutAs<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest { PhoneNumber = TestPhones.Thai().E164 });
        var thai = await WithPhoneAsync(TestPhones.Thai(), PrivacyLevel.Everybody);

        (await FindByPhones(viewer, [thai.Phone.National])).ShouldHaveSingleItem().User.Id.ShouldBe(thai.User.Id);
    }

    [Fact]
    public async Task Find_by_phone_excludes_blocks_in_both_directions_self_and_deleted_accounts()
    {
        var viewer = await factory.NewUserAsync();
        var myPhone = TestPhones.Russian();
        await viewer.Client.PutAs<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest { PhoneNumber = myPhone.E164, FindByPhone = PrivacyLevel.Everybody });
        var blocker = await WithPhoneAsync(TestPhones.Thai(), PrivacyLevel.Everybody);
        var blocked = await WithPhoneAsync(TestPhones.Thai(), PrivacyLevel.Everybody);
        var deleted = await WithPhoneAsync(TestPhones.Thai(), PrivacyLevel.Everybody);
        var visible = await WithPhoneAsync(TestPhones.Thai(), PrivacyLevel.Everybody);
        await blocker.User.BlockAsync(viewer);
        await viewer.BlockAsync(blocked.User);
        await (await deleted.User.Client.Post(ApiRoutes.Users.MeDelete, new DeleteAccountRequest { Password = deleted.User.Password }))
            .ShouldBeStatusAsync(HttpStatusCode.NoContent);

        var matches = await FindByPhones(viewer, [myPhone.E164, blocker.Phone.E164, blocked.Phone.E164, deleted.Phone.E164, visible.Phone.E164, "", "garbage"]);

        matches.ShouldHaveSingleItem().User.Id.ShouldBe(visible.User.Id);
    }

    [Fact]
    public async Task Find_by_phone_validates_the_request_size()
    {
        var viewer = await factory.NewUserAsync();
        var tooMany = Enumerable.Range(0, Limits.FindByPhonesMax + 1).Select(i => $"+7999{i:D7}").ToList();

        await (await viewer.Client.Post(ApiRoutes.Users.FindByPhones, new FindByPhonesRequest { PhoneNumbers = tooMany }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        (await FindByPhones(viewer, [])).ShouldBeEmpty();
    }

    [Fact]
    public async Task Phone_lookups_are_rate_limited_per_user()
    {
        var viewer = await factory.NewUserAsync();
        var other = await factory.NewUserAsync();
        for (var i = 0; i < 10; i++)
            await FindByPhones(viewer, [TestPhones.Thai().E164]);

        await (await viewer.Client.Post(ApiRoutes.Users.FindByPhones, new FindByPhonesRequest { PhoneNumbers = [TestPhones.Thai().E164] }))
            .ShouldFailAsync(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        (await FindByPhones(other, [TestPhones.Thai().E164])).ShouldBeEmpty();
    }

    private static string Tag() => "q" + Guid.NewGuid().ToString("N")[..8];

    private static Task<IReadOnlyList<UserSummaryDto>> Search(TestUser viewer, string query, int limit = 30) =>
        viewer.Client.GetAs<IReadOnlyList<UserSummaryDto>>(ApiRoutes.Users.SearchPath(query, limit));

    private static Task<IReadOnlyList<PhoneMatchDto>> FindByPhones(TestUser viewer, IReadOnlyList<string> numbers, string? defaultRegion = null, IReadOnlyList<string>? regions = null) =>
        viewer.Client.PostAs<IReadOnlyList<PhoneMatchDto>>(ApiRoutes.Users.FindByPhones, new FindByPhonesRequest
        {
            PhoneNumbers = numbers,
            DefaultRegion = defaultRegion,
            DefaultRegions = regions ?? [],
        });

    private async Task<(TestUser User, (string E164, string National) Phone)> WithPhoneAsync((string E164, string National) phone, PrivacyLevel findByPhone)
    {
        var user = await factory.NewUserAsync();
        await user.Client.PutAs<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest { PhoneNumber = phone.E164, FindByPhone = findByPhone });
        return (user, phone);
    }
}
