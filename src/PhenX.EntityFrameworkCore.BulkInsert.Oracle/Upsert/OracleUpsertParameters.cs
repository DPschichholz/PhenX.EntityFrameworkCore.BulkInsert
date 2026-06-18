namespace PhenX.EntityFrameworkCore.BulkInsert.Oracle.Upsert;

/// <summary>
/// Shared bind parameter names for the GTT REF CURSOR PL/SQL blocks.
/// </summary>
internal static class OracleUpsertParameters
{
    /// <summary>
    /// Bind name for the run identifier, bound by name (RAW(16)).
    /// </summary>
    public const string BatchId = "batch_id";

    /// <summary>
    /// Bind name for the output REF CURSOR.
    /// </summary>
    public const string RefCursor = "rc";

    /// <summary>
    /// Output alias of CLIENT_ROW_ID in the REF CURSOR result set.
    /// </summary>
    public const string ClientRowIdAlias = "CLIENT_ROW_ID";
}

