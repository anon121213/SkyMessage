using System.Net;
using System.Text;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Identity.IdentityKit;

namespace SkyMessage.Api.IntegrationTests.Identity;

[Collection(ApiCollection.Name)]
public sealed class ProfileTests(ApiFactory factory)
{
    [Fact]
    public async Task Patch_updates_only_the_given_fields_and_trims_them()
    {
        var user = await factory.NewUserAsync(displayName: "Original");

        var renamed = await user.Client.PatchAs<MeDto>(ApiRoutes.Users.Me, new UpdateProfileRequest { DisplayName = "  New Name  " });
        renamed.DisplayName.ShouldBe("New Name");
        renamed.Bio.ShouldBeNull();
        renamed.Username.ShouldBe(user.Username);

        var withBio = await user.Client.PatchAs<MeDto>(ApiRoutes.Users.Me, new UpdateProfileRequest { Bio = "  Family chat admin  " });
        withBio.Bio.ShouldBe("Family chat admin");
        withBio.DisplayName.ShouldBe("New Name");

        var cleared = await user.Client.PatchAs<MeDto>(ApiRoutes.Users.Me, new UpdateProfileRequest { Bio = "" });
        cleared.Bio.ShouldBeNull();

        var me = await user.Client.GetAs<MeDto>(ApiRoutes.Users.Me);
        me.DisplayName.ShouldBe("New Name");
        me.Bio.ShouldBeNull();
        me.SavedMessagesChatId.ShouldBe(user.Auth.User.SavedMessagesChatId);
    }

    [Fact]
    public async Task Patch_rejects_invalid_names_and_bios()
    {
        var user = await factory.NewUserAsync();

        await (await user.Client.Patch(ApiRoutes.Users.Me, new UpdateProfileRequest { DisplayName = "   " }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await user.Client.Patch(ApiRoutes.Users.Me, new UpdateProfileRequest { DisplayName = new string('n', Limits.DisplayNameMaxLength + 1) }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await user.Client.Patch(ApiRoutes.Users.Me, new UpdateProfileRequest { Bio = new string('b', Limits.BioMaxLength + 1) }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        (await user.Client.GetAs<MeDto>(ApiRoutes.Users.Me)).DisplayName.ShouldBe("Test User");
    }

    [Fact]
    public async Task Username_change_validates_format_reservation_and_uniqueness()
    {
        var user = await factory.NewUserAsync(NewUsername("Name"));
        var other = await factory.NewUserAsync(NewUsername("Taken"));

        await (await user.Client.Patch(ApiRoutes.Users.Me, new UpdateProfileRequest { Username = "x1" }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.UsernameInvalid);
        await (await user.Client.Patch(ApiRoutes.Users.Me, new UpdateProfileRequest { Username = "support" }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.UsernameInvalid);
        await (await user.Client.Patch(ApiRoutes.Users.Me, new UpdateProfileRequest { Username = other.Username.ToLowerInvariant() }))
            .ShouldFailAsync(HttpStatusCode.Conflict, ErrorCodes.UsernameTaken);

        // Changing only the case of one's own username is fine.
        var recased = await user.Client.PatchAs<MeDto>(ApiRoutes.Users.Me, new UpdateProfileRequest { Username = user.Username.ToUpperInvariant() });
        recased.Username.ShouldBe(user.Username.ToUpperInvariant());

        var fresh = NewUsername("Fresh");
        var changed = await user.Client.PatchAs<MeDto>(ApiRoutes.Users.Me, new UpdateProfileRequest { Username = "@" + fresh });
        changed.Username.ShouldBe(fresh);
        (await other.Client.GetAs<UserProfileDto>(ApiRoutes.Users.ByUsernamePath(fresh))).User.Id.ShouldBe(user.Id);
        await (await other.Client.Get(ApiRoutes.Users.ByUsernamePath(user.Username))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.UserNotFound);
        // The old name is free again.
        (await factory.NewUserAsync(user.Username)).Id.ShouldNotBe(user.Id);
        // Signing in uses the new name.
        await (await factory.TryLoginAsync(fresh, user.Password)).ShouldBeStatusAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Concurrent_renames_to_one_username_let_only_one_win()
    {
        var users = await factory.NewUsersAsync(4);
        var wanted = NewUsername("Wanted");

        var results = await Task.WhenAll(users.Select(u => u.Client.Patch(ApiRoutes.Users.Me, new UpdateProfileRequest { Username = wanted })));

        results.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        foreach (var loser in results.Where(r => r.StatusCode != HttpStatusCode.OK))
            await loser.ShouldFailAsync(HttpStatusCode.Conflict, ErrorCodes.UsernameTaken);
    }

    [Fact]
    public async Task Profile_changes_notify_related_users_and_the_users_own_devices()
    {
        var user = await factory.NewUserAsync();
        var otherDevice = await user.SignInAgainAsync();
        var contactOwner = await factory.NewUserAsync();
        var groupMate = await factory.NewUserAsync();
        var stranger = await factory.NewUserAsync();
        await contactOwner.AddContactAsync(user);
        await user.CreateGroupAsync(groupMate);
        await using var ownHub = await RealtimeProbe.ConnectAsync(otherDevice);
        await using var contactHub = await RealtimeProbe.ConnectAsync(contactOwner);
        await using var groupHub = await RealtimeProbe.ConnectAsync(groupMate);
        await using var strangerHub = await RealtimeProbe.ConnectAsync(stranger);

        await user.Client.PatchAs<MeDto>(ApiRoutes.Users.Me, new UpdateProfileRequest { DisplayName = "Renamed" });

        await ownHub.WaitForAsync<UserRefEvent>(e => e.UserId == user.Id);
        await contactHub.WaitForAsync<UserRefEvent>(e => e.UserId == user.Id);
        await groupHub.WaitForAsync<UserRefEvent>(e => e.UserId == user.Id);
        await strangerHub.FlushAsync();
        strangerHub.Received<UserRefEvent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_kind_of_profile_change_sends_UserChanged_and_no_op_changes_do_not()
    {
        var user = await factory.NewUserAsync();
        var watcher = await factory.NewUserAsync();
        await watcher.AddContactAsync(user);
        await using var hub = await RealtimeProbe.ConnectAsync(watcher);
        var photo = await factory.CreateMediaAsync(user.Id);

        var changes = new Func<Task>[]
        {
            () => user.Client.PatchAs<MeDto>(ApiRoutes.Users.Me, new UpdateProfileRequest { Bio = "hello" }),
            () => user.Client.PutAs<MeDto>(ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = photo }),
            async () => await (await user.Client.Delete(ApiRoutes.Users.MeAvatar)).ReadAsync<MeDto>(),
            () => user.Client.PutAs<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest { PhoneNumber = TestPhones.Thai().E164 }),
            async () => await (await user.Client.Delete(ApiRoutes.Users.MePhone)).ReadAsync<MeDto>(),
            () => user.SetPrivacyAsync(p => p with { LastSeen = PrivacyLevel.Contacts }),
        };
        var expected = 0;
        foreach (var change in changes)
        {
            await change();
            expected++;
            await hub.FlushAsync();
            hub.Received<UserRefEvent>(e => e.UserId == user.Id).Count.ShouldBe(expected);
        }

        // Repeating the same values changes nothing and stays silent.
        await user.Client.PatchAs<MeDto>(ApiRoutes.Users.Me, new UpdateProfileRequest { Bio = "hello" });
        await (await user.Client.Delete(ApiRoutes.Users.MeAvatar)).ReadAsync<MeDto>();
        await (await user.Client.Delete(ApiRoutes.Users.MePhone)).ReadAsync<MeDto>();
        await user.SetPrivacyAsync(p => p with { LastSeen = PrivacyLevel.Contacts });
        await hub.FlushAsync();
        hub.Received<UserRefEvent>(e => e.UserId == user.Id).Count.ShouldBe(expected);
    }

    [Fact]
    public async Task Avatar_must_be_the_callers_own_ready_photo()
    {
        var user = await factory.NewUserAsync();
        var other = await factory.NewUserAsync();
        var foreignPhoto = await factory.CreateMediaAsync(other.Id);
        var file = await factory.CreateMediaAsync(user.Id, MediaKind.File);
        // Uploading, not Processing: a media worker would pick up a Processing row that has no chunks.
        var notReady = await factory.CreateMediaAsync(user.Id, MediaKind.Photo, MediaStatus.Uploading);
        var photo = await factory.CreateMediaAsync(user.Id);

        await (await user.Client.Put(ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = foreignPhoto }))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        await (await user.Client.Put(ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = Guid.CreateVersion7() }))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.MediaNotFound);
        await (await user.Client.Put(ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = file }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.MediaUnsupported);
        await (await user.Client.Put(ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = notReady }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.MediaNotReady);
        await (await user.Client.Put(ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = Guid.Empty }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        var me = await user.Client.PutAs<MeDto>(ApiRoutes.Users.MeAvatar, new SetAvatarRequest { MediaId = photo });
        me.AvatarMediaId.ShouldBe(photo);
        (await other.Client.GetAs<UserProfileDto>(ApiRoutes.Users.ByIdPath(user.Id))).User.AvatarMediaId.ShouldBe(photo);

        var removed = await (await user.Client.Delete(ApiRoutes.Users.MeAvatar)).ReadAsync<MeDto>();
        removed.AvatarMediaId.ShouldBeNull();
        (await other.Client.GetAs<UserProfileDto>(ApiRoutes.Users.ByIdPath(user.Id))).User.AvatarMediaId.ShouldBeNull();
    }

    [Fact]
    public async Task Phone_numbers_are_normalized_to_E164_with_the_right_region()
    {
        var user = await factory.NewUserAsync();
        var thai = TestPhones.Thai();
        var thai2 = TestPhones.Thai();
        var russian = TestPhones.Russian();

        var withRegion = await user.Client.PutAs<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest
        {
            PhoneNumber = thai.National,
            DefaultRegion = "TH",
            Source = PhoneSource.Sim,
        });
        withRegion.PhoneNumber.ShouldBe(thai.E164);
        withRegion.PhoneSource.ShouldBe(PhoneSource.Sim);

        // No region given: the caller's own number's region (TH) is used for the next national number.
        var ownRegion = await user.Client.PutAs<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest { PhoneNumber = thai2.National });
        ownRegion.PhoneNumber.ShouldBe(thai2.E164);
        ownRegion.PhoneSource.ShouldBe(PhoneSource.Manual);

        var international = await user.Client.PutAs<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest { PhoneNumber = russian.E164 });
        international.PhoneNumber.ShouldBe(russian.E164);

        var fresh = await factory.NewUserAsync();
        var russianNational = TestPhones.Russian();
        // Without any region the RU fallback reads "8 (9xx) ...".
        (await fresh.Client.PutAs<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest { PhoneNumber = russianNational.National }))
            .PhoneNumber.ShouldBe(russianNational.E164);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("not a phone")]
    [InlineData("+999 123")]
    public async Task Invalid_phone_numbers_are_rejected(string phone)
    {
        var user = await factory.NewUserAsync();
        await (await user.Client.Put(ApiRoutes.Users.MePhone, new SetPhoneRequest { PhoneNumber = phone }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.PhoneInvalid);
    }

    [Fact]
    public async Task A_phone_number_belongs_to_one_account()
    {
        var owner = await factory.NewUserAsync();
        var other = await factory.NewUserAsync();
        var phone = TestPhones.Thai();
        await owner.Client.PutAs<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest { PhoneNumber = phone.E164 });

        await (await other.Client.Put(ApiRoutes.Users.MePhone, new SetPhoneRequest { PhoneNumber = phone.National, DefaultRegion = "TH" }))
            .ShouldFailAsync(HttpStatusCode.Conflict, ErrorCodes.PhoneTaken);
        // Re-linking one's own number is fine (idempotent).
        (await owner.Client.PutAs<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest { PhoneNumber = phone.National, DefaultRegion = "TH" }))
            .PhoneNumber.ShouldBe(phone.E164);

        var removed = await (await owner.Client.Delete(ApiRoutes.Users.MePhone)).ReadAsync<MeDto>();
        removed.PhoneNumber.ShouldBeNull();
        removed.PhoneSource.ShouldBe(PhoneSource.None);
        (await other.Client.PutAs<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest { PhoneNumber = phone.E164 })).PhoneNumber.ShouldBe(phone.E164);
    }

    [Fact]
    public async Task Setting_the_phone_can_also_enable_find_by_phone()
    {
        var user = await factory.NewUserAsync();
        var me = await user.Client.PutAs<MeDto>(ApiRoutes.Users.MePhone, new SetPhoneRequest
        {
            PhoneNumber = TestPhones.Russian().E164,
            FindByPhone = PrivacyLevel.Everybody,
        });

        me.Privacy.FindByPhone.ShouldBe(PrivacyLevel.Everybody);
        (await user.Client.GetAs<PrivacySettingsDto>(ApiRoutes.Users.MePrivacy)).FindByPhone.ShouldBe(PrivacyLevel.Everybody);
    }

    [Fact]
    public async Task Privacy_settings_round_trip()
    {
        var user = await factory.NewUserAsync();
        (await user.Client.GetAs<PrivacySettingsDto>(ApiRoutes.Users.MePrivacy)).ShouldBe(new PrivacySettingsDto
        {
            LastSeen = PrivacyLevel.Everybody,
            ProfilePhoto = PrivacyLevel.Everybody,
            PhoneNumber = PrivacyLevel.Contacts,
            FindByPhone = PrivacyLevel.Nobody,
            Messages = PrivacyLevel.Everybody,
            Calls = PrivacyLevel.Everybody,
            GroupInvites = PrivacyLevel.Everybody,
        });

        var wanted = new PrivacySettingsDto
        {
            LastSeen = PrivacyLevel.Nobody,
            ProfilePhoto = PrivacyLevel.Contacts,
            PhoneNumber = PrivacyLevel.Everybody,
            FindByPhone = PrivacyLevel.Contacts,
            Messages = PrivacyLevel.Contacts,
            Calls = PrivacyLevel.Nobody,
            GroupInvites = PrivacyLevel.Contacts,
        };
        (await user.Client.PutAs<PrivacySettingsDto>(ApiRoutes.Users.MePrivacy, wanted)).ShouldBe(wanted);
        (await user.Client.GetAs<PrivacySettingsDto>(ApiRoutes.Users.MePrivacy)).ShouldBe(wanted);
        (await user.Client.GetAs<MeDto>(ApiRoutes.Users.Me)).Privacy.ShouldBe(wanted);
    }

    [Fact]
    public async Task Unknown_privacy_levels_are_rejected()
    {
        var user = await factory.NewUserAsync();
        var content = new StringContent("""{"lastSeen":"Friends"}""", Encoding.UTF8, "application/json");
        await (await user.Client.PutAsync(ApiRoutes.Users.MePrivacy, content, Ct)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);

        var numeric = new StringContent("""{"lastSeen":7}""", Encoding.UTF8, "application/json");
        await (await user.Client.PutAsync(ApiRoutes.Users.MePrivacy, numeric, Ct)).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        (await user.Client.GetAs<PrivacySettingsDto>(ApiRoutes.Users.MePrivacy)).LastSeen.ShouldBe(PrivacyLevel.Everybody);
    }
}
