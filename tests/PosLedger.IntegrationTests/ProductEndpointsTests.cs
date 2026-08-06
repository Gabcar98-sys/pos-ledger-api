using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PosLedger.Api.Common;
using PosLedger.Api.Domain;
using PosLedger.Api.Features.Products;
using PosLedger.Api.Persistence;

namespace PosLedger.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class ProductEndpointsTests(PosLedgerApiFactory factory) : IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Creating_a_product_with_initial_stock_writes_an_opening_ledger_entry()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/products",
            new CreateProductRequest("CAF-500", "Ground coffee 500g", 28500m, 40));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await response.Content.ReadFromJsonAsync<ProductResponse>();
        created!.StockOnHand.Should().Be(40);
        created.UnitPrice.Should().Be(28500m);

        // The point of the ledger: the stock figure has to be explainable by movements.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PosLedgerDbContext>();
        var movements = await db.StockMovements.Where(m => m.ProductId == created.Id).ToListAsync();

        movements.Should().ContainSingle();
        movements[0].Delta.Should().Be(40);
        movements[0].Reason.Should().Be(StockMovementReason.Adjustment);
    }

    [Fact]
    public async Task Creating_a_product_without_stock_writes_no_movement()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/products",
            new CreateProductRequest("EMP-001", "Empty shelf item", 1000m, 0));

        var created = await response.Content.ReadFromJsonAsync<ProductResponse>();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PosLedgerDbContext>();

        // A zero-delta row would violate ck_stock_movements_delta_not_zero, and it would also
        // be a lie: nothing moved.
        (await db.StockMovements.CountAsync(m => m.ProductId == created!.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Duplicate_sku_is_rejected_by_the_database_as_a_conflict()
    {
        var request = new CreateProductRequest("DUP-1", "First", 100m, 1);
        (await _client.PostAsJsonAsync("/api/v1/products", request)).StatusCode
            .Should().Be(HttpStatusCode.Created);

        var second = await _client.PostAsJsonAsync("/api/v1/products",
            request with { Name = "Second" });

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await second.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        problem.Should().ContainKey("correlationId");
    }

    [Theory]
    [InlineData("", "Name", 100, 0)]                 // empty sku
    [InlineData("BAD SKU", "Name", 100, 0)]          // space is not allowed in a sku
    [InlineData("OK-1", "", 100, 0)]                 // empty name
    [InlineData("OK-1", "Name", -1, 0)]              // negative price
    [InlineData("OK-1", "Name", 19.999, 0)]          // more precision than the column stores
    [InlineData("OK-1", "Name", 100, -5)]            // negative stock
    public async Task Invalid_payloads_are_rejected_before_reaching_the_database(
        string sku, string name, double price, int stock)
    {
        // price arrives as double because attribute arguments cannot be decimal constants.
        var response = await _client.PostAsJsonAsync("/api/v1/products",
            new CreateProductRequest(sku, name, (decimal)price, stock));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Listing_pages_through_the_catalogue_with_a_keyset_cursor()
    {
        foreach (var index in Enumerable.Range(1, 7))
        {
            await _client.PostAsJsonAsync("/api/v1/products",
                new CreateProductRequest($"SKU-{index:D2}", $"Item {index}", 1000m, 5));
        }

        var first = await _client.GetFromJsonAsync<Page<ProductResponse>>("/api/v1/products?limit=3");
        first!.Items.Should().HaveCount(3);
        first.HasMore.Should().BeTrue();
        first.NextCursor.Should().Be("SKU-03");

        var second = await _client.GetFromJsonAsync<Page<ProductResponse>>(
            $"/api/v1/products?limit=3&after={first.NextCursor}");
        second!.Items.Select(p => p.Sku).Should().Equal("SKU-04", "SKU-05", "SKU-06");

        var third = await _client.GetFromJsonAsync<Page<ProductResponse>>(
            $"/api/v1/products?limit=3&after={second.NextCursor}");
        third!.Items.Should().ContainSingle();
        third.HasMore.Should().BeFalse();
        third.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task Search_matches_name_and_sku_case_insensitively()
    {
        await _client.PostAsJsonAsync("/api/v1/products",
            new CreateProductRequest("CAF-500", "Ground coffee 500g", 28500m, 10));
        await _client.PostAsJsonAsync("/api/v1/products",
            new CreateProductRequest("MUG-STD", "Ceramic mug", 18000m, 10));

        var byName = await _client.GetFromJsonAsync<Page<ProductResponse>>("/api/v1/products?q=COFFEE");
        byName!.Items.Should().ContainSingle().Which.Sku.Should().Be("CAF-500");

        var bySku = await _client.GetFromJsonAsync<Page<ProductResponse>>("/api/v1/products?q=mug");
        bySku!.Items.Should().ContainSingle().Which.Sku.Should().Be("MUG-STD");
    }

    [Fact]
    public async Task Inactive_products_are_hidden_unless_asked_for()
    {
        var created = await (await _client.PostAsJsonAsync("/api/v1/products",
            new CreateProductRequest("OLD-1", "Discontinued", 500m, 0))).Content
            .ReadFromJsonAsync<ProductResponse>();

        var update = await _client.PutAsJsonAsync($"/api/v1/products/{created!.Id}",
            new UpdateProductRequest("Discontinued", 500m, IsActive: false));
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        (await _client.GetFromJsonAsync<Page<ProductResponse>>("/api/v1/products"))!
            .Items.Should().BeEmpty();

        (await _client.GetFromJsonAsync<Page<ProductResponse>>("/api/v1/products?includeInactive=true"))!
            .Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Unknown_product_returns_a_problem_document_not_an_empty_200()
    {
        var response = await _client.GetAsync($"/api/v1/products/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Health_does_not_depend_on_the_database_and_ready_does()
    {
        (await _client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync("/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
