using PhenX.EntityFrameworkCore.BulkInsert.Dialect;
using PhenX.EntityFrameworkCore.BulkInsert.Metadata;

namespace PhenX.EntityFrameworkCore.BulkInsert.Oracle.Upsert;

/// <summary>
/// Mapping information between an entity and its Oracle bulk staging Global Temporary Table (GTT).
/// </summary>
internal sealed class OracleGlobalTemporaryTableMetadata
{
    public OracleGlobalTemporaryTableMetadata(
        string tableName,
        string quotedTableName,
        IReadOnlyList<StagingColumnMetadata> columns)
    {
        TableName = tableName;
        QuotedTableName = quotedTableName;
        Columns = columns;
        BatchIdColumn = columns.Single(c => c.ColumnName == OracleGttColumns.BatchId);
        ClientRowIdColumn = columns.Single(c => c.ColumnName == OracleGttColumns.ClientRowId);
        PayloadColumns = columns.Where(c => !c.IsTechnical).ToList();
    }

    public string TableName { get; }

    public string QuotedTableName { get; }

    /// <summary>
    /// All GTT columns in BulkCopy order: technical columns first, then the entity payload columns.
    /// </summary>
    public IReadOnlyList<StagingColumnMetadata> Columns { get; }

    public IReadOnlyList<StagingColumnMetadata> PayloadColumns { get; }

    public StagingColumnMetadata BatchIdColumn { get; }

    public StagingColumnMetadata ClientRowIdColumn { get; }
}

/// <summary>
/// Builds <see cref="OracleGlobalTemporaryTableMetadata"/> from EF Core table metadata, adding the
/// technical <see cref="OracleGttColumns.BatchId"/> and <see cref="OracleGttColumns.ClientRowId"/>
/// columns and producing GTT-compatible (always nullable, identity-stripped) store types.
/// </summary>
internal sealed class OracleGlobalTemporaryTableMetadataProvider
{
    private const string Prefix = "BULK_";
    private const string Suffix = "_STG";

    // Oracle prior to 12.2 limits identifiers to 30 characters; stay within that bound.
    private const int MaxIdentifierLength = 128;

    private readonly SqlDialectBuilder _dialect;

    public OracleGlobalTemporaryTableMetadataProvider(SqlDialectBuilder dialect)
    {
        _dialect = dialect;
    }

    /// <summary>
    /// Derives the deterministic GTT name for a target table, honoring the optional override.
    /// The target table name is taken verbatim from the EF Core model (no case folding) so the GTT
    /// name follows the same identifier conventions as the mapped table.
    /// </summary>
    public string GetStagingTableName(string targetTableName, string? overrideName = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideName))
        {
            return overrideName!;
        }

        var name = $"{Prefix}{targetTableName}{Suffix}";
        if (name.Length <= MaxIdentifierLength)
        {
            return name;
        }

        var available = MaxIdentifierLength - Prefix.Length - Suffix.Length;
        var trimmed = targetTableName.Length > available ? targetTableName[..available] : targetTableName;
        return $"{Prefix}{trimmed}{Suffix}";
    }

    public OracleGlobalTemporaryTableMetadata Create(
        TableMetadata tableInfo,
        IReadOnlyList<ColumnMetadata> payloadColumns,
        string? overrideName = null)
    {
        var stagingTableName = GetStagingTableName(tableInfo.TableName, overrideName);

        var columns = new List<StagingColumnMetadata>
        {
            new(OracleGttColumns.BatchId, OracleGttColumns.BatchIdStoreType, isTechnical: true, source: null),
            new(OracleGttColumns.ClientRowId, OracleGttColumns.ClientRowIdStoreType, isTechnical: true, source: null),
        };

        foreach (var column in payloadColumns)
        {
            columns.Add(new StagingColumnMetadata(
                column.ColumnName,
                ToGttStoreType(column),
                isTechnical: false,
                source: column));
        }

        return new OracleGlobalTemporaryTableMetadata(
            stagingTableName,
            _dialect.QuoteTableName(null, stagingTableName),
            columns);
    }

    /// <summary>
    /// Produces a GTT-compatible store type: the column's base store type, never carrying an identity
    /// clause and always staged as a plain (nullable) column so partial payloads can be written.
    /// </summary>
    private static string ToGttStoreType(ColumnMetadata column)
    {
        // ColumnMetadata.StoreType is the bare provider type (e.g. "NUMBER(19)", "VARCHAR2(200)",
        // "RAW(16)", "TIMESTAMP(7)"). Identity/computed/default semantics live on the target table,
        // not on the type, so the staging column only needs the raw type.
        return column.StoreType;
    }
}

