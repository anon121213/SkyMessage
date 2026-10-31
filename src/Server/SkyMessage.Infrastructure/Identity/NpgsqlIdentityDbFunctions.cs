using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Identity;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Infrastructure.Identity;

/// <summary>ILIKE predicates; <c>users.display_name</c> has a gin_trgm_ops index for these.</summary>
internal sealed class NpgsqlIdentityDbFunctions : IIdentityDbFunctions
{
    public Expression<Func<User, bool>> DisplayNameMatches(string likePattern) =>
        u => EF.Functions.ILike(u.DisplayName, likePattern, IdentitySearch.EscapeCharacter);

    public Expression<Func<Contact, bool>> ContactNameMatches(string likePattern) =>
        c => c.CustomName != null && EF.Functions.ILike(c.CustomName, likePattern, IdentitySearch.EscapeCharacter);
}
