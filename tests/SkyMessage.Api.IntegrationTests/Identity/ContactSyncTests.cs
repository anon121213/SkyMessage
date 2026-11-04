using System.Net;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Identity;

/// <summary>
/// Phone-book sync adds contacts marked as synced; "remove synced contacts" takes only those, never people the user
/// added, imported or renamed by hand.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ContactSyncTests(ApiFactory factory)
{
    [Fact]
    public async Task Synced_contacts_are_marked_and_people_already_in_contacts_stay_as_they_are()
    {
        var owner = await factory.NewUserAsync();
        var (anna, oleg) = (await factory.NewUserAsync(), await factory.NewUserAsync());
        await owner.AddContactAsync(anna, "Аня");

        var added = await owner.Client.PostAs<IReadOnlyList<ContactDto>>(ApiRoutes.Contacts.Bulk, new AddContactsBulkRequest
        {
            Synced = true,
            Contacts = [new AddContactRequest { UserId = anna.Id, CustomName = "Anna B." }, new AddContactRequest { UserId = oleg.Id, CustomName = "Олег" }],
        });

        added.Single(c => c.User.Id == oleg.Id).IsSynced.ShouldBeTrue();
        var annaContact = added.Single(c => c.User.Id == anna.Id);
        annaContact.IsSynced.ShouldBeFalse();
        annaContact.CustomName.ShouldBe("Аня");
        (await Contacts(owner)).Single(c => c.User.Id == oleg.Id).IsSynced.ShouldBeTrue();
    }

    [Fact]
    public async Task A_sync_renames_the_contacts_it_added_but_never_the_users_own()
    {
        var owner = await factory.NewUserAsync();
        var (mine, synced) = (await factory.NewUserAsync(), await factory.NewUserAsync());
        await owner.AddContactAsync(mine, "Брат");
        await owner.Client.PostAs<IReadOnlyList<ContactDto>>(ApiRoutes.Contacts.Bulk, new AddContactsBulkRequest
        {
            Synced = true,
            Contacts = [new AddContactRequest { UserId = synced.Id, CustomName = "Оля" }],
        });

        // The phone book renamed both people.
        var result = await owner.Client.PostAs<IReadOnlyList<ContactDto>>(ApiRoutes.Contacts.Bulk, new AddContactsBulkRequest
        {
            Synced = true,
            Contacts = [new AddContactRequest { UserId = mine.Id, CustomName = "Саша" }, new AddContactRequest { UserId = synced.Id, CustomName = "Ольга" }],
        });

        result.Single(c => c.User.Id == synced.Id).CustomName.ShouldBe("Ольга");
        var contacts = await Contacts(owner);
        contacts.Single(c => c.User.Id == synced.Id).ShouldSatisfyAllConditions(c => c.CustomName.ShouldBe("Ольга"), c => c.IsSynced.ShouldBeTrue());
        contacts.Single(c => c.User.Id == mine.Id).ShouldSatisfyAllConditions(c => c.CustomName.ShouldBe("Брат"), c => c.IsSynced.ShouldBeFalse());
    }

    [Fact]
    public async Task Removing_synced_contacts_keeps_the_ones_added_by_hand()
    {
        var owner = await factory.NewUserAsync();
        var (manual, synced, other) = (await factory.NewUserAsync(), await factory.NewUserAsync(), await factory.NewUserAsync());
        await owner.AddContactAsync(manual);
        await SyncAsync(owner, synced, other);
        await using var probe = await RealtimeProbe.ConnectAsync(owner);

        var result = await owner.Client.DeleteAs<RemoveSyncedContactsResultDto>(ApiRoutes.Contacts.Synced);

        result.Removed.ShouldBe(2);
        (await Contacts(owner)).Select(c => c.User.Id).ShouldBe([manual.Id]);
        await probe.WaitForAsync<ContactsChangedEvent>();
        (await owner.Client.DeleteAs<RemoveSyncedContactsResultDto>(ApiRoutes.Contacts.Synced)).Removed.ShouldBe(0);
    }

    [Fact]
    public async Task Adding_importing_or_renaming_a_synced_contact_makes_it_the_users_own()
    {
        var owner = await factory.NewUserAsync();
        var (added, imported, renamed) = (await factory.NewUserAsync(), await factory.NewUserAsync(), await factory.NewUserAsync());
        await SyncAsync(owner, added, imported, renamed);

        await owner.AddContactAsync(added);
        await owner.Client.PostAs<IReadOnlyList<ContactDto>>(ApiRoutes.Contacts.Bulk, new AddContactsBulkRequest
        {
            Contacts = [new AddContactRequest { UserId = imported.Id }],
        });
        (await owner.Client.PatchAs<ContactDto>(ApiRoutes.Contacts.ByUserIdPath(renamed.Id), new UpdateContactRequest { CustomName = "Брат" }))
            .IsSynced.ShouldBeFalse();

        (await Contacts(owner)).ShouldAllBe(c => !c.IsSynced);
        (await owner.Client.DeleteAs<RemoveSyncedContactsResultDto>(ApiRoutes.Contacts.Synced)).Removed.ShouldBe(0);
        (await Contacts(owner)).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Removing_synced_contacts_needs_a_session()
    {
        var anonymous = factory.CreateJsonClient();

        (await anonymous.DeleteAsync(ApiRoutes.Contacts.Synced, IdentityKit.Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static Task<IReadOnlyList<ContactDto>> SyncAsync(TestUser owner, params TestUser[] found) =>
        owner.Client.PostAs<IReadOnlyList<ContactDto>>(ApiRoutes.Contacts.Bulk, new AddContactsBulkRequest
        {
            Synced = true,
            Contacts = found.Select(u => new AddContactRequest { UserId = u.Id }).ToList(),
        });

    private static Task<IReadOnlyList<ContactDto>> Contacts(TestUser owner) =>
        owner.Client.GetAs<IReadOnlyList<ContactDto>>(ApiRoutes.Contacts.Root);
}
