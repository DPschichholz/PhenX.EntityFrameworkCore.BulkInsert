using PhenX.EntityFrameworkCore.BulkInsert.Metadata;

namespace PhenX.EntityFrameworkCore.BulkInsert.Oracle.Upsert;

/// <summary>
/// Describes a single column of an Oracle bulk staging Global Temporary Table (GTT).
/// </summary>
internal sealed class StagingColumnMetadata
{
    public StagingColumnMetadata(string columnName, string storeType, bool isTechnical, ColumnMetadata? source)
    {
        ColumnName = columnName;
        StoreType = storeType;
        IsTechnical = isTechnical;
        Source = source;
    }

    /// <summary>
    /// The unquoted GTT column name.
    /// </summary>
    public string ColumnName { get; }

    /// <summary>
    /// The Oracle store type used in the GTT, always nullable so partial payloads can be staged.
    /// </summary>
    public string StoreType { get; }

    /// <summary>
    /// True for the technical columns <see cref="OracleGttColumns.BatchId"/> and
    /// <see cref="OracleGttColumns.ClientRowId"/>.
    /// </summary>
    public bool IsTechnical { get; }

    /// <summary>
    /// The originating entity column, or <c>null</c> for technical columns.
    /// </summary>
    public ColumnMetadata? Source { get; }
}

