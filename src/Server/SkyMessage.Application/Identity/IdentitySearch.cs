using System.Linq.Expressions;
using System.Text;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Application.Identity;

/// <summary>
/// Provider-specific name predicates for user search (PostgreSQL ILIKE, backed by the trigram index on
/// display names). Implemented by Infrastructure so the Application layer stays provider-neutral.
/// Patterns are complete LIKE patterns escaped with <see cref="IdentitySearch.EscapeCharacter"/>.
/// </summary>
public interface IIdentityDbFunctions
{
    Expression<Func<User, bool>> DisplayNameMatches(string likePattern);

    /// <summary>Contacts without a custom name never match.</summary>
    Expression<Func<Contact, bool>> ContactNameMatches(string likePattern);
}

public static class IdentitySearch
{
    public const string EscapeCharacter = "\\";

    /// <summary>"%term%" with LIKE wildcards in the term escaped, so user input matches literally.</summary>
    public static string ContainsPattern(string term)
    {
        var builder = new StringBuilder(term.Length + 8).Append('%');
        foreach (var c in term)
        {
            if (c is '%' or '_' or '\\') builder.Append('\\');
            builder.Append(c);
        }
        return builder.Append('%').ToString();
    }

    /// <summary><c>left || right</c> over one parameter (EF-translatable).</summary>
    public static Expression<Func<T, bool>> OrElse<T>(this Expression<Func<T, bool>> left, Expression<Func<T, bool>> right)
    {
        var parameter = left.Parameters[0];
        var rightBody = new ParameterReplacer(right.Parameters[0], parameter).Visit(right.Body);
        return Expression.Lambda<Func<T, bool>>(Expression.OrElse(left.Body, rightBody), parameter);
    }

    private sealed class ParameterReplacer(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
