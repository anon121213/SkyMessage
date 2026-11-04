using System.Net;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Identity.IdentityKit;

namespace SkyMessage.Api.IntegrationTests.Identity;

/// <summary>Birthday (with its own privacy level), profile color, cover and links: what others see of them.</summary>
[Collection(ApiCollection.Name)]
public sealed class ProfileExtrasTests(ApiFactory factory)
{
    private static readonly SetBirthdayRequest May9 = new() { Day = 9, Month = 5, Year = 1990 };

    [Theory]
    [InlineData(PrivacyLevel.Everybody, true, true)]
    [InlineData(PrivacyLevel.Contacts, false, true)]
    [InlineData(PrivacyLevel.Nobody, false, false)]
    public async Task Birthday_follows_its_privacy_level(PrivacyLevel level, bool strangerSees, bool contactSees)
    {
        var (owner, stranger, contact) = await ThreeUsersAsync();
        await owner.Client.PutAs<MeDto>(ApiRoutes.Users.MeBirthday, May9);
        await owner.SetPrivacyAsync(p => p with { Birthday = level });
        // The contact keeps the owner in their address book too: their contact list carries it as well.
        await contact.AddContactAsync(owner);

        var expected = new BirthdayDto { Day = 9, Month = 5, Year = 1990 };
        (await Profile(stranger, owner)).User.Birthday.ShouldBe(strangerSees ? expected : null);
        (await Profile(contact, owner)).User.Birthday.ShouldBe(contactSees ? expected : null);
        (await Batch(stranger, owner)).Birthday.ShouldBe(strangerSees ? expected : null);
        (await Batch(contact, owner)).Birthday.ShouldBe(contactSees ? expected : null);
        (await contact.Client.GetAs<IReadOnlyList<ContactDto>>(ApiRoutes.Contacts.Root)).Single(c => c.User.Id == owner.Id)
            .User.Birthday.ShouldBe(contactSees ? expected : null);
        (await Profile(owner, owner)).User.Birthday.ShouldBe(expected);
        (await owner.Client.GetAs<MeDto>(ApiRoutes.Users.Me)).Birthday.ShouldBe(expected);
    }

    [Fact]
    public async Task Birthday_is_for_contacts_by_default_and_apps_that_do_not_know_it_keep_the_level()
    {
        var owner = await factory.NewUserAsync();
        (await owner.Client.GetAs<PrivacySettingsDto>(ApiRoutes.Users.MePrivacy)).Birthday.ShouldBe(PrivacyLevel.Contacts);
        await owner.SetPrivacyAsync(p => p with { Birthday = PrivacyLevel.Everybody });

        // An app from before birthdays sends the privacy settings without the field.
        var old = new
        {
            LastSeen = PrivacyLevel.Contacts,
            ProfilePhoto = PrivacyLevel.Everybody,
            PhoneNumber = PrivacyLevel.Contacts,
            FindByPhone = PrivacyLevel.Nobody,
            Messages = PrivacyLevel.Everybody,
            Calls = PrivacyLevel.Everybody,
            GroupInvites = PrivacyLevel.Everybody,
        };
        var saved = await owner.Client.PutAs<PrivacySettingsDto>(ApiRoutes.Users.MePrivacy, old);

        saved.LastSeen.ShouldBe(PrivacyLevel.Contacts);
        saved.Birthday.ShouldBe(PrivacyLevel.Everybody);
        (await owner.Client.GetAs<PrivacySettingsDto>(ApiRoutes.Users.MePrivacy)).Birthday.ShouldBe(PrivacyLevel.Everybody);
    }

    [Theory]
    [InlineData(29, 2, null, true)]
    [InlineData(29, 2, 2024, true)]
    [InlineData(29, 2, 2023, false)]
    [InlineData(31, 4, null, false)]
    [InlineData(0, 1, null, false)]
    [InlineData(1, 13, null, false)]
    [InlineData(1, 1, 1899, false)]
    [InlineData(1, 1, 3000, false)]
    public async Task Birthday_must_be_a_real_date_in_the_past(int day, int month, int? year, bool accepted)
    {
        var owner = await factory.NewUserAsync();

        var response = await owner.Client.Put(ApiRoutes.Users.MeBirthday, new SetBirthdayRequest { Day = day, Month = month, Year = year });

        if (accepted)
            (await response.ReadAsync<MeDto>()).Birthday.ShouldBe(new BirthdayDto { Day = day, Month = month, Year = year });
        else
            await response.ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
    }

    [Fact]
    public async Task Removing_the_birthday_hides_it_and_tells_contacts()
    {
        var owner = await factory.NewUserAsync();
        var friend = await factory.NewUserAsync();
        await friend.AddContactAsync(owner);
        await owner.AddContactAsync(friend);
        await owner.Client.PutAs<MeDto>(ApiRoutes.Users.MeBirthday, new SetBirthdayRequest { Day = 1, Month = 6 });
        await using var probe = await RealtimeProbe.ConnectAsync(friend);

        var me = await owner.Client.DeleteAs<MeDto>(ApiRoutes.Users.MeBirthday);

        me.Birthday.ShouldBeNull();
        await probe.WaitForAsync<UserRefEvent>(e => e.UserId == owner.Id);
        (await Batch(friend, owner)).Birthday.ShouldBeNull();
    }

    [Fact]
    public async Task Color_and_links_are_shown_to_others_and_checked()
    {
        var owner = await factory.NewUserAsync();
        var viewer = await factory.NewUserAsync();

        var me = await owner.Client.PatchAs<MeDto>(ApiRoutes.Users.Me, new UpdateProfileRequest
        {
            ProfileColor = "teal",
            Links = [new ProfileLinkDto { Url = " example.com/anna ", Title = " Мой сайт " }, new ProfileLinkDto { Url = "https://t.example.org" }],
        });

        me.ProfileColor.ShouldBe("teal");
        me.Links.ShouldBe([new ProfileLinkDto { Url = "https://example.com/anna", Title = "Мой сайт" }, new ProfileLinkDto { Url = "https://t.example.org" }]);
        var seen = await Profile(viewer, owner);
        seen.User.ProfileColor.ShouldBe("teal");
        seen.Links.Count.ShouldBe(2);
        (await Batch(viewer, owner)).ProfileColor.ShouldBe("teal");

        await (await owner.Client.Patch(ApiRoutes.Users.Me, new UpdateProfileRequest { ProfileColor = "neon" }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await owner.Client.Patch(ApiRoutes.Users.Me, new UpdateProfileRequest { Links = [new ProfileLinkDto { Url = "javascript:alert(1)" }] }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await owner.Client.Patch(ApiRoutes.Users.Me, new UpdateProfileRequest { Links = [new ProfileLinkDto { Url = "hello" }] }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        var tooMany = Enumerable.Range(1, Limits.ProfileLinksMax + 1).Select(i => new ProfileLinkDto { Url = $"https://site{i}.example" }).ToList();
        await (await owner.Client.Patch(ApiRoutes.Users.Me, new UpdateProfileRequest { Links = tooMany }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        // Null leaves things alone; empty values clear them.
        (await owner.Client.PatchAs<MeDto>(ApiRoutes.Users.Me, new UpdateProfileRequest { Bio = "hi" })).ProfileColor.ShouldBe("teal");
        var cleared = await owner.Client.PatchAs<MeDto>(ApiRoutes.Users.Me, new UpdateProfileRequest { ProfileColor = "", Links = [] });
        cleared.ProfileColor.ShouldBeNull();
        cleared.Links.ShouldBeEmpty();
        (await Profile(viewer, owner)).Links.ShouldBeEmpty();
    }

    [Fact]
    public async Task Gradient_cover_is_for_everyone_and_a_photo_cover_follows_the_photo_privacy()
    {
        var (owner, stranger, contact) = await ThreeUsersAsync();
        await owner.SetPrivacyAsync(p => p with { ProfilePhoto = PrivacyLevel.Contacts });

        (await owner.Client.PutAs<MeDto>(ApiRoutes.Users.MeCover, new SetCoverRequest { Gradient = "dusk" })).Cover
            .ShouldBe(new ProfileCoverDto { Gradient = "dusk" });
        (await Profile(stranger, owner)).Cover.ShouldBe(new ProfileCoverDto { Gradient = "dusk" });

        var photo = await factory.CreateMediaAsync(owner.Id);
        (await owner.Client.PutAs<MeDto>(ApiRoutes.Users.MeCover, new SetCoverRequest { MediaId = photo })).Cover
            .ShouldBe(new ProfileCoverDto { MediaId = photo });
        (await Profile(contact, owner)).Cover.ShouldBe(new ProfileCoverDto { MediaId = photo });
        (await Profile(stranger, owner)).Cover.ShouldBeNull();
        await (await contact.Client.Get(ApiRoutes.Media.ByIdPath(photo))).ShouldBeStatusAsync(HttpStatusCode.OK);
        await (await stranger.Client.Get(ApiRoutes.Media.ByIdPath(photo))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);

        var someoneElses = await factory.CreateMediaAsync(stranger.Id);
        await (await owner.Client.Put(ApiRoutes.Users.MeCover, new SetCoverRequest { MediaId = someoneElses }))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        await (await owner.Client.Put(ApiRoutes.Users.MeCover, new SetCoverRequest { Gradient = "rainbow" }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await owner.Client.Put(ApiRoutes.Users.MeCover, new SetCoverRequest { Gradient = "dusk", MediaId = photo }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        (await owner.Client.DeleteAs<MeDto>(ApiRoutes.Users.MeCover)).Cover.ShouldBeNull();
        (await Profile(contact, owner)).Cover.ShouldBeNull();
    }

    [Fact]
    public async Task Someone_who_blocked_the_viewer_shows_no_birthday_color_cover_or_links()
    {
        var owner = await factory.NewUserAsync();
        var viewer = await factory.NewUserAsync();
        await owner.AddContactAsync(viewer);
        await owner.SetPrivacyAsync(p => p with { Birthday = PrivacyLevel.Everybody });
        await owner.Client.PutAs<MeDto>(ApiRoutes.Users.MeBirthday, May9);
        await owner.Client.PutAs<MeDto>(ApiRoutes.Users.MeCover, new SetCoverRequest { Gradient = "night" });
        await owner.Client.PatchAs<MeDto>(ApiRoutes.Users.Me, new UpdateProfileRequest
        {
            ProfileColor = "pink",
            Links = [new ProfileLinkDto { Url = "https://example.com" }],
        });
        (await Profile(viewer, owner)).User.Birthday.ShouldNotBeNull();

        await owner.BlockAsync(viewer);

        var profile = await Profile(viewer, owner);
        profile.User.Birthday.ShouldBeNull();
        profile.User.ProfileColor.ShouldBeNull();
        profile.Cover.ShouldBeNull();
        profile.Links.ShouldBeEmpty();
        (await Batch(viewer, owner)).Birthday.ShouldBeNull();
    }

    [Fact]
    public async Task Deleting_the_account_clears_birthday_style_and_links()
    {
        var owner = await factory.NewUserAsync();
        var viewer = await factory.NewUserAsync();
        await owner.SetPrivacyAsync(p => p with { Birthday = PrivacyLevel.Everybody });
        await owner.Client.PutAs<MeDto>(ApiRoutes.Users.MeBirthday, May9);
        await owner.Client.PatchAs<MeDto>(ApiRoutes.Users.Me, new UpdateProfileRequest { ProfileColor = "red", Links = [new ProfileLinkDto { Url = "https://a.example" }] });

        await (await owner.Client.Post(ApiRoutes.Users.MeDelete, new DeleteAccountRequest { Password = owner.Password }))
            .ShouldBeStatusAsync(HttpStatusCode.NoContent);

        var stored = await factory.LoadUserAsync(owner.Id);
        stored.BirthDay.ShouldBeNull();
        stored.BirthYear.ShouldBeNull();
        stored.ProfileColor.ShouldBeNull();
        stored.Links.ShouldBeEmpty();
        (await Batch(viewer, owner)).Birthday.ShouldBeNull();
    }

    private async Task<(TestUser Owner, TestUser Stranger, TestUser Contact)> ThreeUsersAsync()
    {
        var owner = await factory.NewUserAsync();
        var stranger = await factory.NewUserAsync();
        var contact = await factory.NewUserAsync();
        await owner.AddContactAsync(contact);
        return (owner, stranger, contact);
    }

    private static Task<UserProfileDto> Profile(TestUser viewer, TestUser owner) =>
        viewer.Client.GetAs<UserProfileDto>(ApiRoutes.Users.ByIdPath(owner.Id));

    private static async Task<UserSummaryDto> Batch(TestUser viewer, TestUser owner) =>
        (await viewer.Client.PostAs<IReadOnlyList<UserSummaryDto>>(ApiRoutes.Users.Batch, new GetUsersRequest { UserIds = [owner.Id] })).Single();
}

internal static class ProfileHttp
{
    public static async Task<T> DeleteAs<T>(this HttpClient client, string path) => await (await client.Delete(path)).ReadAsync<T>();
}
