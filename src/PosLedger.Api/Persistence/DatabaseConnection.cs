using Microsoft.AspNetCore.WebUtilities;
using Npgsql;

namespace PosLedger.Api.Persistence;

/// <summary>
/// Resolves the Postgres connection string from either shape the app is deployed with:
/// an ADO.NET string locally, or the <c>postgres://user:pass@host/db</c> URL that every
/// managed provider hands out (Neon, Render, Supabase, Heroku). Doing this here means the
/// deployment target never needs a bespoke settings file.
/// </summary>
public static class DatabaseConnection
{
    public static string Resolve(IConfiguration configuration)
    {
        var url = configuration["DATABASE_URL"];

        if (!string.IsNullOrWhiteSpace(url) &&
            url.StartsWith("postgres", StringComparison.OrdinalIgnoreCase))
        {
            return FromUrl(url);
        }

        return configuration.GetConnectionString("Postgres")
               ?? throw new InvalidOperationException(
                   "No database configured. Set DATABASE_URL or ConnectionStrings:Postgres.");
    }

    private static string FromUrl(string url)
    {
        var uri = new Uri(url);
        var credentials = uri.UserInfo.Split(':', 2);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = uri.AbsolutePath.TrimStart('/'),
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : null,

            // Managed providers terminate idle connections; a pooled connection that the
            // server already closed surfaces as a random 500 on the next request.
            KeepAlive = 30,
            Timeout = 15,
            CommandTimeout = 30
        };

        // Local Postgres in docker compose has no TLS; anything remote must have it.
        var query = QueryHelpers.ParseQuery(uri.Query);
        var sslMode = query.TryGetValue("sslmode", out var value) ? value.ToString() : null;

        builder.SslMode = sslMode?.ToLowerInvariant() switch
        {
            "disable" => SslMode.Disable,
            "allow" => SslMode.Allow,
            "prefer" => SslMode.Prefer,
            "require" => SslMode.Require,
            "verify-ca" => SslMode.VerifyCA,
            "verify-full" => SslMode.VerifyFull,
            _ => IsLocal(uri.Host) ? SslMode.Disable : SslMode.Require
        };

        return builder.ConnectionString;
    }

    private static bool IsLocal(string host) =>
        host is "localhost" or "127.0.0.1" or "::1" || host.EndsWith(".local", StringComparison.Ordinal)
        || host is "db" or "postgres"; // docker compose service names
}
