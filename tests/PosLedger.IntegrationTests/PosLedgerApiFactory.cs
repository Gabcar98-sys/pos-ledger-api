using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PosLedger.Api.Features.Auth;
using PosLedger.Api.Persistence;
using Testcontainers.PostgreSql;

namespace PosLedger.IntegrationTests;

/// <summary>
/// Boots the real application against a real Postgres in a container.
/// <para>
/// Not an in-memory provider: every behaviour worth testing here is behaviour Postgres
/// provides — the unique constraint on sku, the check constraint on stock, row-level
/// locking under concurrent sales, ILIKE. An in-memory provider passes all of those tests
/// and none of them mean anything.
/// </para>
/// </summary>
public sealed class PosLedgerApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("posledger_test")
        .WithUsername("posledger")
        .WithPassword("posledger")
        .Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());

        // The fixture migrates once, explicitly, below. Leaving it to startup would
        // re-run it per factory and hide failures inside the host boot.
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Database:SeedOnStartup", "false");
        builder.UseEnvironment("Testing");
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PosLedgerDbContext>();
        await db.Database.MigrateAsync();
    }

    /// <summary>
    /// A client carrying a real token obtained from the real endpoint. Minting one directly would
    /// skip the part most likely to break — that the issued token is actually accepted.
    /// </summary>
    public async Task<HttpClient> CreateClientAsAsync(string username, string password)
    {
        var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new { username, password });
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);

        return client;
    }

    public Task<HttpClient> CreateAdminClientAsync() => CreateClientAsAsync("admin", "admin-demo-2026");

    public Task<HttpClient> CreateCashierClientAsync() => CreateClientAsAsync("cashier", "cashier-demo-2026");

    /// <summary>
    /// Empties every table between tests. TRUNCATE rather than a per-test transaction rollback:
    /// the concurrency test needs its writes to be visible to other connections, which a
    /// shared uncommitted transaction would prevent.
    /// </summary>
    public async Task ResetDatabaseAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PosLedgerDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE TABLE stock_movements, sale_lines, sales, idempotency_records, products
                RESTART IDENTITY CASCADE;
            ALTER SEQUENCE sale_number_seq RESTART WITH 1;
            """);
    }

    public new async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await base.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<PosLedgerApiFactory>
{
    public const string Name = "api";
}
