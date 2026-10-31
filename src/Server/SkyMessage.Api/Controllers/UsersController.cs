using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Identity;
using SkyMessage.Contracts;

namespace SkyMessage.Api.Controllers;

[ApiController]
public sealed class UsersController(ProfileService profile, UserDirectoryService directory, ICurrentUser currentUser) : ControllerBase
{
    [HttpGet(ApiRoutes.Users.Me)]
    public Task<MeDto> GetMe(CancellationToken ct) => profile.GetMeAsync(currentUser.UserId, ct);

    [HttpPatch(ApiRoutes.Users.Me)]
    public Task<MeDto> UpdateMe(UpdateProfileRequest request, CancellationToken ct) =>
        profile.UpdateProfileAsync(currentUser.UserId, request, ct);

    /// <summary>Password-confirmed. AccountService is resolved per action: it pulls in other features' services.</summary>
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [HttpPost(ApiRoutes.Users.MeDelete)]
    public async Task<IActionResult> DeleteMe(DeleteAccountRequest request, [FromServices] AccountService accounts, CancellationToken ct)
    {
        await accounts.DeleteAsync(currentUser.UserId, request.Password, ct);
        return NoContent();
    }

    [HttpPut(ApiRoutes.Users.MeAvatar)]
    public Task<MeDto> SetAvatar(SetAvatarRequest request, CancellationToken ct) =>
        profile.SetAvatarAsync(currentUser.UserId, request.MediaId, ct);

    [HttpDelete(ApiRoutes.Users.MeAvatar)]
    public Task<MeDto> RemoveAvatar(CancellationToken ct) => profile.RemoveAvatarAsync(currentUser.UserId, ct);

    [HttpPut(ApiRoutes.Users.MePhone)]
    public Task<MeDto> SetPhone(SetPhoneRequest request, CancellationToken ct) =>
        profile.SetPhoneAsync(currentUser.UserId, request, ct);

    [HttpDelete(ApiRoutes.Users.MePhone)]
    public Task<MeDto> RemovePhone(CancellationToken ct) => profile.RemovePhoneAsync(currentUser.UserId, ct);

    [HttpGet(ApiRoutes.Users.MePrivacy)]
    public Task<PrivacySettingsDto> GetPrivacy(CancellationToken ct) => profile.GetPrivacyAsync(currentUser.UserId, ct);

    [HttpPut(ApiRoutes.Users.MePrivacy)]
    public Task<PrivacySettingsDto> SetPrivacy(PrivacySettingsDto settings, CancellationToken ct) =>
        profile.SetPrivacyAsync(currentUser.UserId, settings, ct);

    [HttpGet(ApiRoutes.Users.ById)]
    public Task<UserProfileDto> GetById(Guid userId, CancellationToken ct) =>
        directory.GetProfileAsync(currentUser.UserId, userId, ct);

    [HttpGet(ApiRoutes.Users.ByUsername)]
    public Task<UserProfileDto> GetByUsername(string username, CancellationToken ct) =>
        directory.GetProfileByUsernameAsync(currentUser.UserId, username, ct);

    [HttpPost(ApiRoutes.Users.Batch)]
    public Task<IReadOnlyList<UserSummaryDto>> GetBatch(GetUsersRequest request, CancellationToken ct) =>
        directory.GetBatchAsync(currentUser.UserId, request.UserIds, ct);

    [EnableRateLimiting(RateLimitPolicies.Search)]
    [HttpGet(ApiRoutes.Users.Search)]
    public Task<IReadOnlyList<UserSummaryDto>> Search([FromQuery] string? q, [FromQuery] int? limit, CancellationToken ct) =>
        directory.SearchAsync(currentUser.UserId, q, limit, ct);

    [EnableRateLimiting(RateLimitPolicies.PhoneLookup)]
    [HttpPost(ApiRoutes.Users.FindByPhones)]
    public Task<IReadOnlyList<PhoneMatchDto>> FindByPhones(FindByPhonesRequest request, CancellationToken ct) =>
        directory.FindByPhonesAsync(currentUser.UserId, request, ct);
}
