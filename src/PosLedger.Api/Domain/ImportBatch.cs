namespace PosLedger.Api.Domain;

/// <summary>
/// One uploaded stock file and what became of it.
/// <para>
/// An import is deliberately not all-or-nothing. A client sends 600 rows with 20 bad ones; refusing
/// the file teaches them nothing and delays the 580 that were fine. The good rows land, the bad
/// ones come back grouped by the rule they broke, with line numbers.
/// </para>
/// </summary>
public sealed class ImportBatch
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string FileName { get; init; }

    public required string UploadedBy { get; init; }

    public int RowsRead { get; set; }

    public int RowsAccepted { get; set; }

    public int RowsRejected { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public List<ImportError> Errors { get; init; } = [];
}

/// <summary>
/// A rejected row. <see cref="Rule"/> is what makes the report useful: 127 errors is noise,
/// "119 unknown_sku, 6 quantity_not_a_number, 2 insufficient_stock" is a work list.
/// </summary>
public sealed class ImportError
{
    public long Id { get; init; }

    public Guid BatchId { get; init; }

    /// <summary>Line number in the uploaded file, counting the header as line 1.</summary>
    public int LineNumber { get; init; }

    public required string Rule { get; init; }

    public required string Message { get; init; }

    /// <summary>The row as it arrived, so the sender can find it in their file.</summary>
    public required string RawLine { get; init; }
}
