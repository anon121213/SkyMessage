using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SkyMessage.Application.Messaging;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Infrastructure.Messaging;

/// <summary>
/// PostgreSQL implementations: ILIKE (served by the gin_trgm_ops indexes on messages.text, chats.title and
/// users.display_name) and row-value comparison for the (updated_at, id) keyset.
/// </summary>
internal sealed class NpgsqlMessagingDbFunctions : IMessagingDbFunctions
{
    private const string Escape = "\\";

    public Expression<Func<Message, bool>> MessageTextMatches(string likePattern) =>
        m => m.Text != null && EF.Functions.ILike(m.Text, likePattern, Escape);

    public Expression<Func<Chat, bool>> ChatTitleMatches(string likePattern) =>
        c => c.Title != null && EF.Functions.ILike(c.Title, likePattern, Escape);

    public Expression<Func<User, bool>> UserDisplayNameMatches(string likePattern) =>
        u => EF.Functions.ILike(u.DisplayName, likePattern, Escape);

    public Expression<Func<Contact, bool>> ContactNameMatches(string likePattern) =>
        c => c.CustomName != null && EF.Functions.ILike(c.CustomName, likePattern, Escape);

    public Expression<Func<Message, bool>> MessageChangedAfter(DateTimeOffset updatedAt, Guid id) =>
        m => EF.Functions.GreaterThan(ValueTuple.Create(m.UpdatedAt, m.Id), ValueTuple.Create(updatedAt, id));
}
