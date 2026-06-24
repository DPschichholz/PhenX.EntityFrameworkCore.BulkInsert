namespace PhenX.EntityFrameworkCore.BulkInsert.Oracle.Upsert;

/// <summary>
/// Technical columns that every Oracle bulk staging Global Temporary Table (GTT) must contain.
/// </summary>
internal static class OracleGttColumns
{
    /// <summary>
    /// Identifies a single upsert/insert run. Stored as RAW(16) (a <see cref="System.Guid"/>).
    /// </summary>
    public const string BatchId = "BATCH_ID";

    /// <summary>
    /// Identifies the originating entity within a batch. Used to write generated values back onto the
    /// correct entity instance, never relying on business keys or row order.
    /// </summary>
    public const string ClientRowId = "CLIENT_ROW_ID";

    /// <summary>
    /// Oracle store type of <see cref="BatchId"/>.
    /// </summary>
    public const string BatchIdStoreType = "RAW(16)";

    /// <summary>
    /// Oracle store type of <see cref="ClientRowId"/>.
    /// </summary>
    public const string ClientRowIdStoreType = "NUMBER(19)";
}

/// <summary>
/// How the primary key (ID) of an entity is produced.
/// </summary>
internal enum OracleIdStrategy
{
    /// <summary>
    /// No dedicated single-column surrogate key handling (e.g. natural/composite key only).
    /// </summary>
    None,

    /// <summary>
    /// The ID is produced by an Oracle sequence; the insert uses SEQ.NEXTVAL.
    /// </summary>
    Sequence,

    /// <summary>
    /// The ID is an Oracle identity column; the insert must not set the ID.
    /// </summary>
    Identity,

    /// <summary>
    /// The ID is produced client-side (EF Core value generator, Guid, custom); the insert takes it
    /// from the staging source.
    /// </summary>
    ClientGenerated,
}

