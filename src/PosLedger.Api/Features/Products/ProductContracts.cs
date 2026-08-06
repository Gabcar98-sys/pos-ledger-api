using FluentValidation;
using PosLedger.Api.Domain;

namespace PosLedger.Api.Features.Products;

public sealed record CreateProductRequest(string Sku, string Name, decimal UnitPrice, int InitialStock);

public sealed record UpdateProductRequest(string Name, decimal UnitPrice, bool IsActive);

public sealed record ProductResponse(
    Guid Id,
    string Sku,
    string Name,
    decimal UnitPrice,
    int StockOnHand,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static ProductResponse From(Product p) =>
        new(p.Id, p.Sku, p.Name, p.UnitPrice, p.StockOnHand, p.IsActive, p.CreatedAt, p.UpdatedAt);
}

public sealed class CreateProductValidator : AbstractValidator<CreateProductRequest>
{
    public CreateProductValidator()
    {
        RuleFor(x => x.Sku)
            .NotEmpty()
            .MaximumLength(64)
            .Matches("^[A-Za-z0-9._-]+$")
            .WithMessage("SKU may only contain letters, digits, dot, underscore and hyphen.");

        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);

        // Two decimals, because that is what the column stores. Accepting 19.999 and
        // silently rounding it to 20.00 is how a catalogue starts disagreeing with an invoice.
        RuleFor(x => x.UnitPrice)
            .GreaterThanOrEqualTo(0)
            .Must(price => decimal.Round(price, 2) == price)
            .WithMessage("Unit price cannot have more than 2 decimal places.");

        RuleFor(x => x.InitialStock).GreaterThanOrEqualTo(0);
    }
}

public sealed class UpdateProductValidator : AbstractValidator<UpdateProductRequest>
{
    public UpdateProductValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);

        RuleFor(x => x.UnitPrice)
            .GreaterThanOrEqualTo(0)
            .Must(price => decimal.Round(price, 2) == price)
            .WithMessage("Unit price cannot have more than 2 decimal places.");
    }
}
