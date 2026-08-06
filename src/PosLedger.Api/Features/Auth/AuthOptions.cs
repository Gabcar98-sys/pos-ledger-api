namespace PosLedger.Api.Features.Auth;

public static class Roles
{
    public const string Admin = "admin";
    public const string Cashier = "cashier";
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>The value shipped in appsettings so the app runs out of the box. Refused in Production.</summary>
    public const string DevelopmentSecret = "development-only-secret-do-not-use-in-production-32+";

    public string Issuer { get; set; } = "pos-ledger-api";
    public string Audience { get; set; } = "pos-ledger-api";
    public string Secret { get; set; } = string.Empty;
    public int ExpiryMinutes { get; set; } = 60;
}

/// <summary>
/// The accounts this demo authenticates against.
/// <para>
/// This is deliberately not a users table with a registration flow. Rolling your own identity
/// provider is how portfolios acquire security holes, and in the real deployment this endpoint is
/// replaced by the client's IdP. What is kept is the part that has to be right either way:
/// passwords are stored as PBKDF2 hashes, never in clear, and verification is constant-time.
/// See docs/adr/0006-authentication.md.
/// </para>
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public List<AuthUser> Users { get; set; } = [];
}

public sealed class AuthUser
{
    public string Username { get; set; } = string.Empty;

    /// <summary>Format: <c>iterations.saltBase64.hashBase64</c>.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public string Role { get; set; } = Roles.Cashier;
}
