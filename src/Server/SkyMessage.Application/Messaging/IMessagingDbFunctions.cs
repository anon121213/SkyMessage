using System.Linq.Expressions;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Messaging;

/// <summary>
/// Provider-specific predicates the messaging queries need (PostgreSQL ILIKE backed by trigram indexes and
/// row-value keyset comparison). Implemented by Infrastructure; the Application layer stays provider-neutral.
/// Patterns are complete LIKE patterns escaped with a backslash (see <c>LikePattern</c>).
/// </summary>
public interface IMessagingDbFunctions
{
    /// <summary>Case-insensitive match of <see cref="Message.Text"/> (null text never matches).</summary>
    Expression<Func<Message, bool>> MessageTextMatches(string likePattern);

    Expression<Func<Chat, bool>> ChatTitleMatches(string likePattern);

    Expression<Func<User, bool>> UserDisplayNameMatches(string likePattern);

    Expression<Func<Contact, bool>> ContactNameMatches(string likePattern);

    /// <summary>Keyset predicate <c>(UpdatedAt, Id) &gt; (updatedAt, id)</c> matching the (chat_id, updated_at, id) index.</summary>
    Expression<Func<Message, bool>> MessageChangedAfter(DateTimeOffset updatedAt, Guid id);
}
