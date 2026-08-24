using System.Security.Cryptography;

namespace Banter.Core;

/// <summary>PBKDF2-SHA256 credential hashing for persisted accounts. Iterations are stored
/// per account so the default can rise without invalidating existing credentials.</summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static (byte[] Hash, byte[] Salt) Hash(string secret, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return (hash, salt);
    }

    public static bool Verify(string secret, byte[] hash, byte[] salt, int iterations)
    {
        var candidate = Rfc2898DeriveBytes.Pbkdf2(secret, salt, iterations, HashAlgorithmName.SHA256, hash.Length);
        return CryptographicOperations.FixedTimeEquals(candidate, hash);
    }
}
