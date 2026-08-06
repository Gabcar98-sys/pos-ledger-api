using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PosLedger.Api.Domain;
using PosLedger.Api.Features.Products;
using PosLedger.Api.Features.Sales;
using PosLedger.Api.Persistence;

namespace PosLedger.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class SaleEndpointsTests(PosLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _admin = default!;
    private HttpClient _cashier = default!;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        _admin = await factory.CreateAdminClientAsync();
        _cashier = await factory.CreateCashierClientAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<ProductResponse> GivenProduct(string sku, decimal price, int stock)
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/products",
            new CreateProductRequest(sku, $"Item {sku}", price, stock));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!;
    }

    private HttpRequestMessage SaleRequest(string idempotencyKey, params (Guid ProductId, int Quantity)[] lines)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sales")
        {
            Content = JsonContent.Create(new CreateSaleRequest(
                lines.Select(l => new SaleLineRequest(l.ProductId, l.Quantity)).ToList()))
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    private async Task<(int StockOnHand, int LedgerSum)> ReadLedger(Guid productId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PosLedgerDbContext>();

        var stock = await db.Products.Where(p => p.Id == productId).Select(p => p.StockOnHand).SingleAsync();
        var ledger = await db.StockMovements.Where(m => m.ProductId == productId).SumAsync(m => m.Delta);

        return (stock, ledger);
    }

    [Fact]
    public async Task A_sale_moves_stock_through_the_ledger_and_totals_its_lines()
    {
        var coffee = await GivenProduct("CAF-500", 28500m, 10);
        var mug = await GivenProduct("MUG-STD", 18000m, 5);

        var response = await _cashier.SendAsync(
            SaleRequest(Guid.NewGuid().ToString(), (coffee.Id, 2), (mug.Id, 1)));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var sale = await response.Content.ReadFromJsonAsync<SaleResponse>();
        sale!.Total.Should().Be(28500m * 2 + 18000m);
        sale.Number.Should().Be(1);
        sale.CashierName.Should().Be("cashier");
        sale.Lines.Should().HaveCount(2);

        var (coffeeStock, coffeeLedger) = await ReadLedger(coffee.Id);
        coffeeStock.Should().Be(8);
        // The invariant the whole design exists to keep: the cached figure equals the ledger.
        coffeeLedger.Should().Be(8);

        var (mugStock, mugLedger) = await ReadLedger(mug.Id);
        mugStock.Should().Be(4);
        mugLedger.Should().Be(4);
    }

    [Fact]
    public async Task A_sale_line_keeps_the_price_and_name_it_was_sold_at()
    {
        var product = await GivenProduct("CAF-500", 28500m, 10);

        var sale = await (await _cashier.SendAsync(
                SaleRequest(Guid.NewGuid().ToString(), (product.Id, 1))))
            .Content.ReadFromJsonAsync<SaleResponse>();

        // The catalogue changes after the sale.
        await _admin.PutAsJsonAsync($"/api/v1/products/{product.Id}",
            new UpdateProductRequest("Renamed and repriced", 99000m, IsActive: true));

        var reread = await _cashier.GetFromJsonAsync<SaleResponse>($"/api/v1/sales/{sale!.Id}");

        reread!.Lines[0].UnitPrice.Should().Be(28500m);
        reread.Lines[0].ProductName.Should().Be("Item CAF-500");
        reread.Total.Should().Be(28500m);
    }

    [Fact]
    public async Task Selling_more_than_there_is_leaves_the_stock_untouched()
    {
        var product = await GivenProduct("CAF-500", 28500m, 3);

        var response = await _cashier.SendAsync(
            SaleRequest(Guid.NewGuid().ToString(), (product.Id, 4)));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("available 3");

        var (stock, ledger) = await ReadLedger(product.Id);
        stock.Should().Be(3);
        ledger.Should().Be(3);
    }

    [Fact]
    public async Task Replaying_the_same_key_returns_the_first_sale_and_charges_once()
    {
        var product = await GivenProduct("CAF-500", 28500m, 10);
        var key = Guid.NewGuid().ToString();

        var first = await _cashier.SendAsync(SaleRequest(key, (product.Id, 3)));
        var second = await _cashier.SendAsync(SaleRequest(key, (product.Id, 3)));

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        second.Headers.GetValues("Idempotent-Replay").Should().ContainSingle().Which.Should().Be("true");

        var firstSale = await first.Content.ReadFromJsonAsync<SaleResponse>();
        var secondSale = await second.Content.ReadFromJsonAsync<SaleResponse>();
        secondSale!.Id.Should().Be(firstSale!.Id);
        secondSale.Number.Should().Be(firstSale.Number);

        // The whole point: the retry did not sell three more.
        var (stock, ledger) = await ReadLedger(product.Id);
        stock.Should().Be(7);
        ledger.Should().Be(7);
    }

    [Fact]
    public async Task Line_order_does_not_make_it_a_different_request()
    {
        var a = await GivenProduct("AAA-1", 1000m, 10);
        var b = await GivenProduct("BBB-1", 2000m, 10);
        var key = Guid.NewGuid().ToString();

        await _cashier.SendAsync(SaleRequest(key, (a.Id, 1), (b.Id, 2)));
        var replay = await _cashier.SendAsync(SaleRequest(key, (b.Id, 2), (a.Id, 1)));

        replay.Headers.Contains("Idempotent-Replay").Should().BeTrue();
        (await ReadLedger(a.Id)).StockOnHand.Should().Be(9);
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_basket_is_rejected()
    {
        var product = await GivenProduct("CAF-500", 28500m, 10);
        var key = Guid.NewGuid().ToString();

        await _cashier.SendAsync(SaleRequest(key, (product.Id, 1)));
        var different = await _cashier.SendAsync(SaleRequest(key, (product.Id, 5)));

        different.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReadLedger(product.Id)).StockOnHand.Should().Be(9);
    }

    [Fact]
    public async Task A_sale_without_an_idempotency_key_is_refused()
    {
        var product = await GivenProduct("CAF-500", 28500m, 10);

        var response = await _cashier.PostAsJsonAsync("/api/v1/sales",
            new CreateSaleRequest([new SaleLineRequest(product.Id, 1)]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadLedger(product.Id)).StockOnHand.Should().Be(10);
    }

    [Fact]
    public async Task Selling_an_unknown_or_inactive_product_is_rejected()
    {
        var unknown = await _cashier.SendAsync(SaleRequest(Guid.NewGuid().ToString(), (Guid.NewGuid(), 1)));
        unknown.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var product = await GivenProduct("OLD-1", 1000m, 5);
        await _admin.PutAsJsonAsync($"/api/v1/products/{product.Id}",
            new UpdateProductRequest($"Item OLD-1", 1000m, IsActive: false));

        var inactive = await _cashier.SendAsync(SaleRequest(Guid.NewGuid().ToString(), (product.Id, 1)));
        inactive.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReadLedger(product.Id)).StockOnHand.Should().Be(5);
    }

    [Fact]
    public async Task The_same_product_twice_on_one_sale_is_rejected()
    {
        var product = await GivenProduct("CAF-500", 28500m, 10);

        var response = await _cashier.SendAsync(
            SaleRequest(Guid.NewGuid().ToString(), (product.Id, 1), (product.Id, 1)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Selling_without_a_token_is_rejected()
    {
        var product = await GivenProduct("CAF-500", 28500m, 10);
        var anonymous = factory.CreateClient();

        var response = await anonymous.SendAsync(SaleRequest(Guid.NewGuid().ToString(), (product.Id, 1)));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The test this repository exists to be able to point at.
    /// <para>
    /// Fifty tills try to sell the last ten units at the same time. Exactly ten must succeed and
    /// forty must be told there is no stock — not "roughly ten", and never eleven. The guarantee
    /// comes from <c>SELECT … FOR UPDATE</c> inside the transaction: every request queues on the
    /// same row and reads the stock the previous one committed.
    /// </para>
    /// <para>
    /// Written against a real Postgres because there is nothing to test otherwise: an in-memory
    /// provider has no row locks, so this test would pass on a completely broken implementation.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Fifty_concurrent_sales_of_the_last_ten_units_oversell_nothing()
    {
        const int stock = 10;
        const int attempts = 50;

        var product = await GivenProduct("SCARCE-1", 5000m, stock);

        var responses = await Task.WhenAll(Enumerable.Range(0, attempts).Select(_ =>
            _cashier.SendAsync(SaleRequest(Guid.NewGuid().ToString(), (product.Id, 1)))));

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var conflicted = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        created.Should().Be(stock, "exactly the available units may be sold");
        conflicted.Should().Be(attempts - stock, "every other attempt must be refused, not queued");
        responses.Should().OnlyContain(r =>
            r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.Conflict);

        var (remaining, ledger) = await ReadLedger(product.Id);
        remaining.Should().Be(0);
        ledger.Should().Be(0);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PosLedgerDbContext>();

        (await db.Sales.CountAsync()).Should().Be(stock);
        (await db.StockMovements.CountAsync(m => m.Reason == StockMovementReason.Sale)).Should().Be(stock);

        // Sale numbers are unique even though ten transactions asked for one at the same moment.
        var numbers = await db.Sales.Select(s => s.Number).ToListAsync();
        numbers.Should().OnlyHaveUniqueItems();
    }
}
