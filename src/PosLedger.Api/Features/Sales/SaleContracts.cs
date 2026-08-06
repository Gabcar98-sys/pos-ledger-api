using FluentValidation;
using PosLedger.Api.Domain;

namespace PosLedger.Api.Features.Sales;

public sealed record SaleLineRequest(Guid ProductId, int Quantity);

public sealed record CreateSaleRequest(IReadOnlyList<SaleLineRequest> Lines);

public sealed record SaleLineResponse(
    Guid ProductId,
    string Sku,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal);

public sealed record SaleResponse(
    Guid Id,
    long Number,
    string CashierName,
    decimal Total,
    DateTimeOffset OccurredAt,
    IReadOnlyList<SaleLineResponse> Lines)
{
    public static SaleResponse From(Sale sale) =>
        new(sale.Id, sale.Number, sale.CashierName, sale.Total, sale.OccurredAt,
            sale.Lines
                .Select(l => new SaleLineResponse(l.ProductId, l.Sku, l.ProductName, l.Quantity, l.UnitPrice, l.LineTotal))
                .ToList());
}

public sealed class CreateSaleValidator : AbstractValidator<CreateSaleRequest>
{
    public CreateSaleValidator()
    {
        RuleFor(x => x.Lines)
            .NotEmpty().WithMessage("A sale needs at least one line.")
            .Must(lines => lines.Count <= 200).WithMessage("A sale cannot have more than 200 lines.");

        // Two lines for the same product would take two separate locks on the same row and
        // decrement it twice; collapsing them is the client's job, not a silent fix here.
        RuleFor(x => x.Lines)
            .Must(lines => lines.Select(l => l.ProductId).Distinct().Count() == lines.Count)
            .WithMessage("The same product appears on more than one line.")
            .When(x => x.Lines is { Count: > 0 });

        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ProductId).NotEmpty();
            line.RuleFor(l => l.Quantity).InclusiveBetween(1, 10_000);
        });
    }
}
