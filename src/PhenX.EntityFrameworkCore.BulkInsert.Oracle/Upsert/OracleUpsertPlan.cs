using PhenX.EntityFrameworkCore.BulkInsert.Metadata;

namespace PhenX.EntityFrameworkCore.BulkInsert.Oracle.Upsert;

/// <summary>
/// Fully resolved description of a single Oracle GTT-based upsert or insert run, consumed by the
/// SQL generators. Built by the dialect/adapter from EF Core metadata and the conflict options so the
/// generators stay pure string builders.
/// </summary>
internal sealed class OracleUpsertPlan
{
    public required OracleGlobalTemporaryTableMetadata Gtt { get; init; }

    public required string TargetQuotedTableName { get; init; }

    /// <summary>
    /// Quoted match column names (single or composite). Required for an upsert/merge; for a plain
    /// insert it is only used by the REF CURSOR join when present.
    /// </summary>
    public required IReadOnlyList<string> MatchColumns { get; init; }

    /// <summary>
    /// Payload columns written by the INSERT branch (excludes the ID column when it is database
    /// generated via sequence or identity).
    /// </summary>
    public required IReadOnlyList<ColumnMetadata> InsertColumns { get; init; }

    /// <summary>
    /// Update assignments for the WHEN MATCHED branch, already rewritten to the <c>tgt.</c>/<c>src.</c>
    /// aliases. Empty for insert-only or DoNothing.
    /// </summary>
    public IReadOnlyList<string> UpdateClauses { get; init; } = [];

    /// <summary>
    /// Optional trailing condition for the matched update, rewritten to <c>tgt.</c>/<c>src.</c> aliases.
    /// </summary>
    public string? UpdateWhere { get; init; }

    /// <summary>
    /// The single-column primary key, when present.
    /// </summary>
    public ColumnMetadata? IdColumn { get; init; }

    public OracleIdStrategy IdStrategy { get; init; } = OracleIdStrategy.None;

    /// <summary>
    /// Sequence name (unquoted) used when <see cref="IdStrategy"/> is
    /// <see cref="OracleIdStrategy.Sequence"/>.
    /// </summary>
    public string? SequenceName { get; init; }

    /// <summary>
    /// Columns selected back through the REF CURSOR in addition to CLIENT_ROW_ID and the ID.
    /// </summary>
    public IReadOnlyList<ColumnMetadata> ReturnedColumns { get; init; } = [];
}

