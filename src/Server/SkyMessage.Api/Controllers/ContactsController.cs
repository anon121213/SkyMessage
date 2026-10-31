using Microsoft.AspNetCore.Mvc;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Identity;
using SkyMessage.Contracts;

namespace SkyMessage.Api.Controllers;

[ApiController]
public sealed class ContactsController(ContactService contacts, ICurrentUser currentUser) : ControllerBase
{
    [HttpGet(ApiRoutes.Contacts.Root)]
    public Task<IReadOnlyList<ContactDto>> List(CancellationToken ct) => contacts.ListAsync(currentUser.UserId, ct);

    [HttpPost(ApiRoutes.Contacts.Root)]
    public Task<ContactDto> Add(AddContactRequest request, CancellationToken ct) =>
        contacts.AddAsync(currentUser.UserId, request, ct);

    [HttpPost(ApiRoutes.Contacts.Bulk)]
    public Task<IReadOnlyList<ContactDto>> AddBulk(AddContactsBulkRequest request, CancellationToken ct) =>
        contacts.AddBulkAsync(currentUser.UserId, request, ct);

    [HttpPatch(ApiRoutes.Contacts.ByUserId)]
    public Task<ContactDto> Update(Guid userId, UpdateContactRequest request, CancellationToken ct) =>
        contacts.UpdateAsync(currentUser.UserId, userId, request, ct);

    [HttpDelete(ApiRoutes.Contacts.ByUserId)]
    public async Task<IActionResult> Remove(Guid userId, CancellationToken ct)
    {
        await contacts.RemoveAsync(currentUser.UserId, userId, ct);
        return NoContent();
    }
}
