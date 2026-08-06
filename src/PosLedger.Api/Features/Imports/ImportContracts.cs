namespace PosLedger.Api.Features.Imports;

/// <summary>The rules a row can break. Named, because a count per rule is a work list.</summary>
public static class ImportRules
{
    public const string ColumnCount = "column_count";
    public const string SkuRequired = "sku_required";
    public const string SkuUnknown = "sku_unknown";
    public const string SkuDuplicated = "sku_duplicated";
    public const string QuantityNotANumber = "quantity_not_a_number";
    public const string QuantityZero = "quantity_zero";
    public const string ReasonInvalid = "reason_invalid";
    public const string ProductInactive = "product_inactive";
    public const string InsufficientStock = "insufficient_stock";
}

public sealed record ImportErrorSample(int LineNumber, string Message, string RawLine);

public sealed record ImportErrorGroup(string Rule, int Count, IReadOnlyList<ImportErrorSample> Samples);

public sealed record ImportReport(
    Guid Id,
    string FileName,
    int RowsRead,
    int RowsAccepted,
    int RowsRejected,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ImportErrorGroup> Errors);
