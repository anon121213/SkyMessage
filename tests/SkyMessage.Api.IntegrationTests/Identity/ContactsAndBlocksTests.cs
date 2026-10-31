using System.Net;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;
using static SkyMessage.Api.IntegrationTests.Identity.IdentityKit;

namespace SkyMessage.Api.IntegrationTests.Identity;

[Collection(ApiCollection.Name)]
public sealed class ContactsAndBlocksTests(ApiFactory factory)
{
    [Fact]
    public async Task Adding_a_contact_is_idempotent_and_reports_mutuality()
    {
        var owner = await factory.NewUserAsync();
        var friend = await factory.NewUserAsync(displayName: "Friend");

        var added = await owner.Client.PostAs<ContactDto>(ApiRoutes.Contacts.Root, new AddContactRequest { UserId = friend.Id, CustomName = "  Bestie  " });
        added.User.Id.ShouldBe(friend.Id);
        added.CustomName.ShouldBe("Bestie");
        added.User.ContactName.ShouldBe("Bestie");
        added.User.IsContact.ShouldBeTrue();
        added.IsMutual.ShouldBeFalse();

        var again = await owner.Client.PostAs<ContactDto>(ApiRoutes.Contacts.Root, new AddContactRequest { UserId = friend.Id, CustomName = "Renamed" });
        again.CustomName.ShouldBe("Renamed");
        again.AddedAt.ShouldBe(added.AddedAt);

        await friend.AddContactAsync(owner);
        var list = await owner.Client.GetAs<IReadOnlyList<ContactDto>>(ApiRoutes.Contacts.Root);
        var only = list.ShouldHaveSingleItem();
        only.User.Id.ShouldBe(friend.Id);
        only.IsMutual.ShouldBeTrue();
        only.CustomName.ShouldBe("Renamed");
    }

    [Fact]
    public async Task Contacts_cannot_target_self_unknown_or_deleted_users()
    {
        var owner = await factory.NewUserAsync();
        var gone = await factory.NewUserAsync();
        await (await gone.Client.Post(ApiRoutes.Users.MeDelete, new DeleteAccountRequest { Password = gone.Password })).ShouldBeStatusAsync(HttpStatusCode.NoContent);

        await (await owner.Client.Post(ApiRoutes.Contacts.Root, new AddContactRequest { UserId = owner.Id }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.CannotTargetSelf);
        await (await owner.Client.Post(ApiRoutes.Contacts.Root, new AddContactRequest { UserId = Guid.CreateVersion7() }))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.UserNotFound);
        await (await owner.Client.Post(ApiRoutes.Contacts.Root, new AddContactRequest { UserId = gone.Id }))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.UserNotFound);
        await (await owner.Client.Post(ApiRoutes.Contacts.Root, new AddContactRequest { UserId = Guid.Empty }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        await (await owner.Client.Patch(ApiRoutes.Contacts.ByUserIdPath(owner.Id), new UpdateContactRequest { CustomName = "me" }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.CannotTargetSelf);
        await (await owner.Client.Delete(ApiRoutes.Contacts.ByUserIdPath(owner.Id)))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.CannotTargetSelf);
        await (await owner.Client.Post(ApiRoutes.Contacts.Root, new AddContactRequest { UserId = gone.Id, CustomName = new string('n', Limits.ContactNameMaxLength + 1) }))
            .ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
        (await owner.Client.GetAs<IReadOnlyList<ContactDto>>(ApiRoutes.Contacts.Root)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Bulk_import_skips_unknown_users_and_self_and_keeps_existing_names()
    {
        var owner = await factory.NewUserAsync();
        var named = await factory.NewUserAsync();
        var unnamed = await factory.NewUserAsync();
        var fresh = await factory.NewUserAsync();
        await owner.AddContactAsync(named, "Keep me");
        await owner.AddContactAsync(unnamed);

        var result = await owner.Client.PostAs<IReadOnlyList<ContactDto>>(ApiRoutes.Contacts.Bulk, new AddContactsBulkRequest
        {
            Contacts =
            [
                new AddContactRequest { UserId = named.Id, CustomName = "Phone book name" },
                new AddContactRequest { UserId = unnamed.Id, CustomName = "Now named" },
                new AddContactRequest { UserId = fresh.Id, CustomName = "Fresh" },
                new AddContactRequest { UserId = Guid.CreateVersion7(), CustomName = "Ghost" },
                new AddContactRequest { UserId = owner.Id, CustomName = "Me" },
            ],
        });

        result.Select(c => (c.User.Id, c.CustomName)).ShouldBe(
            [(named.Id, "Keep me"), (unnamed.Id, "Now named"), (fresh.Id, "Fresh")], ignoreOrder: true);
        (await owner.Client.GetAs<IReadOnlyList<ContactDto>>(ApiRoutes.Contacts.Root)).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Renaming_and_removing_contacts()
    {
        var owner = await factory.NewUserAsync();
        var friend = await factory.NewUserAsync(displayName: "Zoe");
        var other = await factory.NewUserAsync(displayName: "Adam");

        // PATCH on someone who is not a contact yet adds them.
        var added = await owner.Client.PatchAs<ContactDto>(ApiRoutes.Contacts.ByUserIdPath(friend.Id), new UpdateContactRequest { CustomName = "Aunt Zoe" });
        added.CustomName.ShouldBe("Aunt Zoe");
        await owner.AddContactAsync(other);

        var list = await owner.Client.GetAs<IReadOnlyList<ContactDto>>(ApiRoutes.Contacts.Root);
        list.Select(c => c.User.Id).ShouldBe([other.Id, friend.Id]); // sorted by shown name: "Adam" < "Aunt Zoe"

        var cleared = await owner.Client.PatchAs<ContactDto>(ApiRoutes.Contacts.ByUserIdPath(friend.Id), new UpdateContactRequest { CustomName = "  " });
        cleared.CustomName.ShouldBeNull();
        cleared.User.ContactName.ShouldBeNull();

        await (await owner.Client.Delete(ApiRoutes.Contacts.ByUserIdPath(friend.Id))).ShouldBeStatusAsync(HttpStatusCode.NoContent);
        await (await owner.Client.Delete(ApiRoutes.Contacts.ByUserIdPath(friend.Id))).ShouldBeStatusAsync(HttpStatusCode.NoContent);
        (await owner.Client.GetAs<IReadOnlyList<ContactDto>>(ApiRoutes.Contacts.Root)).ShouldHaveSingleItem().User.Id.ShouldBe(other.Id);
        await (await owner.Client.Patch(ApiRoutes.Contacts.ByUserIdPath(Guid.CreateVersion7()), new UpdateContactRequest { CustomName = "x" }))
            .ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task Contact_changes_notify_the_owners_devices_and_the_affected_user()
    {
        var owner = await factory.NewUserAsync();
        var ownerTablet = await owner.SignInAgainAsync();
        var target = await factory.NewUserAsync();
        await target.AddContactAsync(owner); // so the target's "mutual" flag changes as well
        await using var tabletHub = await RealtimeProbe.ConnectAsync(ownerTablet);
        await using var targetHub = await RealtimeProbe.ConnectAsync(target);

        await owner.AddContactAsync(target, "T");
        await tabletHub.WaitForAsync<ContactsChangedEvent>(e => !e.BlocksChanged);
        await targetHub.WaitForAsync<UserRefEvent>(e => e.UserId == owner.Id);
        await targetHub.WaitForAsync<ContactsChangedEvent>();

        // A rename concerns only the owner.
        await tabletHub.FlushAsync();
        await targetHub.FlushAsync();
        var before = (tabletHub.Received<ContactsChangedEvent>().Count, targetHub.Received<UserRefEvent>().Count, targetHub.Received<ContactsChangedEvent>().Count);
        await owner.Client.PatchAs<ContactDto>(ApiRoutes.Contacts.ByUserIdPath(target.Id), new UpdateContactRequest { CustomName = "Renamed" });
        await tabletHub.FlushAsync();
        await targetHub.FlushAsync();
        tabletHub.Received<ContactsChangedEvent>().Count.ShouldBe(before.Item1 + 1);
        targetHub.Received<UserRefEvent>().Count.ShouldBe(before.Item2);
        targetHub.Received<ContactsChangedEvent>().Count.ShouldBe(before.Item3);

        // Removing notifies both again; repeating a no-op is silent.
        await (await owner.Client.Delete(ApiRoutes.Contacts.ByUserIdPath(target.Id))).ShouldBeStatusAsync(HttpStatusCode.NoContent);
        await (await owner.Client.Delete(ApiRoutes.Contacts.ByUserIdPath(target.Id))).ShouldBeStatusAsync(HttpStatusCode.NoContent);
        await tabletHub.FlushAsync();
        await targetHub.FlushAsync();
        tabletHub.Received<ContactsChangedEvent>().Count.ShouldBe(before.Item1 + 2);
        targetHub.Received<UserRefEvent>(e => e.UserId == owner.Id).Count.ShouldBe(before.Item2 + 1);
        targetHub.Received<ContactsChangedEvent>().Count.ShouldBe(before.Item3 + 1);
    }

    [Fact]
    public async Task Blocks_are_idempotent_listed_and_validated()
    {
        var user = await factory.NewUserAsync();
        var first = await factory.NewUserAsync();
        var second = await factory.NewUserAsync();

        await user.BlockAsync(first);
        await user.BlockAsync(first);
        await user.BlockAsync(second);

        var blocked = await user.Client.GetAs<IReadOnlyList<UserSummaryDto>>(ApiRoutes.Blocks.Root);
        blocked.Select(b => b.Id).ShouldBe([second.Id, first.Id]); // newest first
        (await first.Client.GetAs<IReadOnlyList<UserSummaryDto>>(ApiRoutes.Blocks.Root)).ShouldBeEmpty();

        await (await user.Client.Put(ApiRoutes.Blocks.ByUserIdPath(user.Id))).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.CannotTargetSelf);
        await (await user.Client.Delete(ApiRoutes.Blocks.ByUserIdPath(user.Id))).ShouldFailAsync(HttpStatusCode.BadRequest, ErrorCodes.CannotTargetSelf);
        await (await user.Client.Put(ApiRoutes.Blocks.ByUserIdPath(Guid.CreateVersion7()))).ShouldFailAsync(HttpStatusCode.NotFound, ErrorCodes.UserNotFound);

        await (await user.Client.Delete(ApiRoutes.Blocks.ByUserIdPath(first.Id))).ShouldBeStatusAsync(HttpStatusCode.NoContent);
        await (await user.Client.Delete(ApiRoutes.Blocks.ByUserIdPath(first.Id))).ShouldBeStatusAsync(HttpStatusCode.NoContent);
        (await user.Client.GetAs<IReadOnlyList<UserSummaryDto>>(ApiRoutes.Blocks.Root)).ShouldHaveSingleItem().Id.ShouldBe(second.Id);
    }

    [Fact]
    public async Task Block_changes_notify_both_sides()
    {
        var blocker = await factory.NewUserAsync();
        var blockerPhone = await blocker.SignInAgainAsync();
        var target = await factory.NewUserAsync();
        await using var blockerHub = await RealtimeProbe.ConnectAsync(blockerPhone);
        await using var targetHub = await RealtimeProbe.ConnectAsync(target);

        await blocker.BlockAsync(target);

        await blockerHub.WaitForAsync<ContactsChangedEvent>(e => e.BlocksChanged);
        await blockerHub.WaitForAsync<UserRefEvent>(e => e.UserId == target.Id);
        await targetHub.WaitForAsync<UserRefEvent>(e => e.UserId == blocker.Id);
        await targetHub.FlushAsync();
        targetHub.Received<ContactsChangedEvent>().ShouldBeEmpty();

        // A repeated block changes nothing and is silent.
        await blocker.BlockAsync(target);
        await blockerHub.FlushAsync();
        blockerHub.Received<ContactsChangedEvent>().Count.ShouldBe(1);

        await (await blocker.Client.Delete(ApiRoutes.Blocks.ByUserIdPath(target.Id))).ShouldBeStatusAsync(HttpStatusCode.NoContent);
        await blockerHub.FlushAsync();
        await targetHub.FlushAsync();
        blockerHub.Received<ContactsChangedEvent>(e => e.BlocksChanged).Count.ShouldBe(2);
        targetHub.Received<UserRefEvent>(e => e.UserId == blocker.Id).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Contacts_of_deleted_accounts_disappear_from_the_list()
    {
        var owner = await factory.NewUserAsync();
        var gone = await factory.NewUserAsync();
        await owner.AddContactAsync(gone);

        await (await gone.Client.Post(ApiRoutes.Users.MeDelete, new DeleteAccountRequest { Password = gone.Password })).ShouldBeStatusAsync(HttpStatusCode.NoContent);

        (await owner.Client.GetAs<IReadOnlyList<ContactDto>>(ApiRoutes.Contacts.Root)).ShouldBeEmpty();
    }
}
