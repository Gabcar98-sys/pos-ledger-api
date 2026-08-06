using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PosLedger.Api.Persistence;

namespace PosLedger.UnitTests;

/// <summary>
/// The deployment target hands over a URL; Npgsql wants an ADO.NET string. This is the seam
/// where that mismatch is resolved, so it is the seam worth pinning down.
/// </summary>
public sealed class DatabaseConnectionTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void Parses_a_managed_provider_url_into_a_connection_string()
    {
        var resolved = DatabaseConnection.Resolve(
            Config(("DATABASE_URL", "postgres://alice:s3cret@db.example.com:6543/appdb")));

        var builder = new NpgsqlConnectionStringBuilder(resolved);
        builder.Host.Should().Be("db.example.com");
        builder.Port.Should().Be(6543);
        builder.Database.Should().Be("appdb");
        builder.Username.Should().Be("alice");
        builder.Password.Should().Be("s3cret");
    }

    [Fact]
    public void Defaults_to_port_5432_when_the_url_omits_it()
    {
        var resolved = DatabaseConnection.Resolve(
            Config(("DATABASE_URL", "postgres://alice:s3cret@db.example.com/appdb")));

        new NpgsqlConnectionStringBuilder(resolved).Port.Should().Be(5432);
    }

    [Fact]
    public void Percent_encoded_credentials_survive_the_round_trip()
    {
        // Generated passwords contain @ and /, which is exactly what breaks a naive split.
        var resolved = DatabaseConnection.Resolve(
            Config(("DATABASE_URL", "postgres://alice:p%40ss%2Fword@db.example.com/appdb")));

        new NpgsqlConnectionStringBuilder(resolved).Password.Should().Be("p@ss/word");
    }

    [Fact]
    public void Remote_hosts_require_tls_by_default()
    {
        var resolved = DatabaseConnection.Resolve(
            Config(("DATABASE_URL", "postgres://alice:s3cret@db.example.com/appdb")));

        new NpgsqlConnectionStringBuilder(resolved).SslMode.Should().Be(SslMode.Require);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("db")]
    public void Local_hosts_do_not(string host)
    {
        var resolved = DatabaseConnection.Resolve(
            Config(("DATABASE_URL", $"postgres://alice:s3cret@{host}/appdb")));

        new NpgsqlConnectionStringBuilder(resolved).SslMode.Should().Be(SslMode.Disable);
    }

    [Fact]
    public void An_explicit_sslmode_in_the_url_wins()
    {
        var resolved = DatabaseConnection.Resolve(
            Config(("DATABASE_URL", "postgres://alice:s3cret@localhost/appdb?sslmode=require")));

        new NpgsqlConnectionStringBuilder(resolved).SslMode.Should().Be(SslMode.Require);
    }

    [Fact]
    public void Falls_back_to_the_ado_net_connection_string()
    {
        var resolved = DatabaseConnection.Resolve(
            Config(("ConnectionStrings:Postgres", "Host=localhost;Database=posledger")));

        resolved.Should().Contain("Host=localhost");
    }

    [Fact]
    public void Missing_configuration_fails_loudly_at_startup_rather_than_on_first_request()
    {
        var act = () => DatabaseConnection.Resolve(Config());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DATABASE_URL*");
    }
}
