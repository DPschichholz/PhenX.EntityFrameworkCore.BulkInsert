namespace PhenX.EntityFrameworkCore.BulkInsert.Oracle.Upsert;

/// <summary>
/// One row returned by the REF CURSOR: the technical CLIENT_ROW_ID plus the generated ID and any
/// optional returned values, in the order declared by the plan.
/// </summary>
internal sealed class UpsertResultRow
{
    public required long ClientRowId { get; init; }

    public object? Id { get; init; }

    public object?[] ReturnedValues { get; init; } = [];
}

