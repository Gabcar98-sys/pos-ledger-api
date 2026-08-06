using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PosLedger.Api.Common;
using PosLedger.Api.Features.Imports;
using PosLedger.Api.Features.Products;
using PosLedger.Api.Features.Reconciliation;
using PosLedger.Api.Features.Sales;
using PosLedger.Api.Persistence;

namespace PosLedger.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class ImportAndReconciliationTests(PosLedgerApiFactory factory) : IAsyncLifetime
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
        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!;
    }

    private async Task<HttpResponseMessage> Upload(string csv, string fileName = "stock.csv")
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", fileName);

        return await _admin.PostAsync("/api/v1/imports", content);
    }

    [Fact]
    public async Task Good_rows_are_applied_and_bad_rows_come_back_grouped_by_rule()
    {
        await GivenProduct("CAF-500", 28500m, 10);
        await GivenProduct("MUG-STD", 18000m, 4);

        var csv = """
                  sku,quantity,reason,note
                  CAF-500,25,Import,march restock
                  MUG-STD,-2,Adjustment,breakage
                  GHOST-1,5,Import,does not exist
                  GHOST-2,7,Import,neither does this
                  CAF-500,3,Import,duplicate of line 2
                  MUG-STD,abc,Import,not a number
                  ,4,Import,no sku
                  """;

        var response = await Upload(csv);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var report = await response.Content.ReadFromJsonAsync<ImportReport>();
        report!.RowsRead.Should().Be(7);
        report.RowsAccepted.Should().Be(2);
        report.RowsRejected.Should().Be(5);

        report.Errors.Should().Contain(g => g.Rule == ImportRules.SkuUnknown && g.Count == 2);
        report.Errors.Should().Contain(g => g.Rule == ImportRules.SkuDuplicated && g.Count == 1);
        report.Errors.Should().Contain(g => g.Rule == ImportRules.QuantityNotANumber && g.Count == 1);
        report.Errors.Should().Contain(g => g.Rule == ImportRules.SkuRequired && g.Count == 1);

        // The line number has to point at the row in the file the sender is looking at.
        var duplicate = report.Errors.Single(g => g.Rule == ImportRules.SkuDuplicated);
        duplicate.Samples[0].LineNumber.Should().Be(6);
        duplicate.Samples[0].RawLine.Should().StartWith("CAF-500,3");

        var coffee = await _admin.GetFromJsonAsync<Page<ProductResponse>>("/api/v1/products?q=CAF-500");
        coffee!.Items[0].StockOnHand.Should().Be(35);

        var mug = await _admin.GetFromJsonAsync<Page<ProductResponse>>("/api/v1/products?q=MUG-STD");
        mug!.Items[0].StockOnHand.Should().Be(2);
    }

    [Fact]
    public async Task An_import_that_would_take_stock_negative_is_refused_row_by_row()
    {
        await GivenProduct("CAF-500", 28500m, 3);

        var report = await (await Upload("sku,quantity\nCAF-500,-10")).Content.ReadFromJsonAsync<ImportReport>();

        report!.RowsAccepted.Should().Be(0);
        report.Errors.Should().ContainSingle().Which.Rule.Should().Be(ImportRules.InsufficientStock);
    }

    [Fact]
    public async Task A_sale_reason_cannot_be_written_from_a_spreadsheet()
    {
        await GivenProduct("CAF-500", 28500m, 10);

        var report = await (await Upload("sku,quantity,reason\nCAF-500,-1,Sale")).Content
            .ReadFromJsonAsync<ImportReport>();

        report!.RowsAccepted.Should().Be(0);
        report.Errors.Should().ContainSingle().Which.Rule.Should().Be(ImportRules.ReasonInvalid);
    }

    [Fact]
    public async Task Columns_are_matched_by_name_so_their_order_does_not_matter()
    {
        await GivenProduct("CAF-500", 28500m, 10);

        var report = await (await Upload("note,quantity,sku\nreordered header,5,CAF-500")).Content
            .ReadFromJsonAsync<ImportReport>();

        report!.RowsAccepted.Should().Be(1);
    }

    [Fact]
    public async Task Quoted_fields_may_contain_commas_and_quotes()
    {
        await GivenProduct("CAF-500", 28500m, 10);

        var report = await (await Upload("sku,quantity,note\nCAF-500,5,\"restock, urgent, said \"\"the boss\"\"\""))
            .Content.ReadFromJsonAsync<ImportReport>();

        report!.RowsAccepted.Should().Be(1);
        report.RowsRejected.Should().Be(0);
    }

    [Fact]
    public async Task A_file_without_the_required_columns_is_refused_whole()
    {
        var response = await Upload("code,amount\nCAF-500,5");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("sku");
    }

    [Fact]
    public async Task The_report_can_be_read_again_later()
    {
        await GivenProduct("CAF-500", 28500m, 10);

        var first = await (await Upload("sku,quantity\nCAF-500,5\nNOPE,1")).Content.ReadFromJsonAsync<ImportReport>();
        var reread = await _admin.GetFromJsonAsync<ImportReport>($"/api/v1/imports/{first!.Id}");

        reread!.RowsAccepted.Should().Be(1);
        reread.RowsRejected.Should().Be(1);
        reread.FileName.Should().Be("stock.csv");
    }

    [Fact]
    public async Task Reconciliation_reports_the_window_and_finds_no_drift_in_a_healthy_ledger()
    {
        var product = await GivenProduct("CAF-500", 28500m, 10);

        var sale = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sales")
        {
            Content = JsonContent.Create(new CreateSaleRequest([new SaleLineRequest(product.Id, 4)]))
        };
        sale.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        await _cashier.SendAsync(sale);

        await Upload("sku,quantity\nCAF-500,6");

        var report = await _admin.GetFromJsonAsync<ReconciliationReport>("/api/v1/reconciliation");

        var line = report!.Products.Should().ContainSingle().Subject;
        line.Sku.Should().Be("CAF-500");
        line.UnitsSold.Should().Be(4);
        line.UnitsReceived.Should().Be(16);   // 10 opening + 6 imported
        line.NetChange.Should().Be(12);
        line.RecordedStock.Should().Be(12);
        line.LedgerStock.Should().Be(12);
        line.Drift.Should().Be(0);

        report.Summary.ProductsWithDrift.Should().Be(0);
        report.Summary.UnitsSold.Should().Be(4);
    }

    /// <summary>
    /// The endpoint is only worth having if it catches the thing it is looking for, so the test
    /// writes stock behind the ledger's back — exactly the bug reconciliation exists to find.
    /// </summary>
    [Fact]
    public async Task Reconciliation_catches_stock_written_without_a_movement()
    {
        var product = await GivenProduct("CAF-500", 28500m, 10);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PosLedgerDbContext>();
            await db.Database.ExecuteSqlAsync(
                $"UPDATE products SET stock_on_hand = 47 WHERE id = {product.Id}");
        }

        var report = await _admin.GetFromJsonAsync<ReconciliationReport>("/api/v1/reconciliation");

        var line = report!.Products.Should().ContainSingle().Subject;
        line.RecordedStock.Should().Be(47);
        line.LedgerStock.Should().Be(10);
        line.Drift.Should().Be(37);
        report.Summary.ProductsWithDrift.Should().Be(1);
    }

    [Fact]
    public async Task Importing_without_the_admin_role_is_rejected()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("sku,quantity\nX,1")), "file", "x.csv");

        (await _cashier.PostAsync("/api/v1/imports", content)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }
}
