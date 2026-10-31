using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using SkyMessage.Application.Identity;
using SkyMessage.Domain.Entities;

namespace SkyMessage.Infrastructure.Identity;

/// <summary>ASP.NET Core Identity's PasswordHasher (PBKDF2-HMAC-SHA512, versioned format with rehash hints).</summary>
internal sealed class IdentityPasswordHasher : IUserPasswordHasher
{
    private readonly PasswordHasher<User> _hasher = new();
    private readonly Lazy<string> _dummyHash;

    public IdentityPasswordHasher()
    {
        _dummyHash = new Lazy<string>(() => _hasher.HashPassword(new User(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))));
    }

    public string Hash(User user, string password) => _hasher.HashPassword(user, password);

    public PasswordCheckResult Verify(User user, string passwordHash, string password)
    {
        if (string.IsNullOrEmpty(passwordHash)) return PasswordCheckResult.Failed;
        try
        {
            return _hasher.VerifyHashedPassword(user, passwordHash, password) switch
            {
                PasswordVerificationResult.Success => PasswordCheckResult.Success,
                PasswordVerificationResult.SuccessRehashNeeded => PasswordCheckResult.SuccessRehashNeeded,
                _ => PasswordCheckResult.Failed,
            };
        }
        catch (FormatException)
        {
            // Corrupted / foreign hash format: treat as a wrong password, never as a server error.
            return PasswordCheckResult.Failed;
        }
    }

    public void VerifyDummy(string password) => _hasher.VerifyHashedPassword(new User(), _dummyHash.Value, password);
}
