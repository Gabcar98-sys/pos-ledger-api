using System.Security.Cryptography;

namespace PosLedger.Api.Features.Auth;

/// <summary>
/// PBKDF2-SHA256. Stored as <c>iterations.saltBase64.hashBase64</c> so the iteration count can be
/// raised later without invalidating existing hashes.
/// </summary>
public static class PasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int DefaultIterations = 100_000;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);

        return $"{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string encoded)
    {
        var parts = encoded.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
        {
            return false;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        // Constant-time: a plain SequenceEqual leaks how many leading bytes matched,
        // which is enough to reconstruct the hash one byte at a time.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
