using System.Text;

using Microsoft.EntityFrameworkCore;

using PhenX.EntityFrameworkCore.BulkInsert.Dialect;
using PhenX.EntityFrameworkCore.BulkInsert.Metadata;
using PhenX.EntityFrameworkCore.BulkInsert.Options;
using PhenX.EntityFrameworkCore.BulkInsert.Oracle.Upsert;

namespace PhenX.EntityFrameworkCore.BulkInsert.Oracle;

internal class OracleDialectBuilder : SqlDialectBuilder
{
    protected override string OpenDelimiter => "\"";
    protected override string CloseDelimiter => "\"";
    protected override string ConcatOperator => "||";

    protected override bool SupportsMoveRows => false;

    // Oracle has no BTRIM function (PostgreSQL-specific); the ANSI TRIM removes leading and trailing spaces.
    protected override string Trim(string lhs) => $"TRIM({lhs})";

    // Oracle (EF Core / ODP.NET) stores System.Guid as RAW(16). A dashed string literal would be
    // implicitly converted via HEXTORAW and fail with ORA-01465 ("invalid hex number"). Emit an
    // explicit HEXTORAW literal using the same byte order ODP.NET uses when reading/writing the value.
    protected override string FormatGuid(Guid value) => $"HEXTORAW('{Convert.ToHexString(value.ToByteArray())}')";

    public override string CreateTableCopySql(string tempTableName, TableMetadata tableInfo, IReadOnlyList<ColumnMetadata> columns)
    {
        return CreateTableCopySqlBase(tempTableName, columns);
    }

    public override string BuildMoveDataSql<T>(
        DbContext context,
        TableMetadata target,
        string source,
        IReadOnlyList<ColumnMetadata> insertedColumns,
        IReadOnlyList<ColumnMetadata> returnedColumns,
        BulkInsertOptions options,
        OnConflictOptions? onConflict = null)
    {
        var q = new StringBuilder();

        // Merge handling
        if (onConflict is OnConflictOptions<T> onConflictTyped)
        {
            // Oracle MERGE doesn't support returning entities
            if (returnedColumns.Count != 0)
            {
                throw new NotSupportedException("Oracle MERGE does not support returning entities. Use ExecuteBulkInsertAsync without returning results when using conflict resolution.");
            }

            IReadOnlyList<string> matchColumns;
            if (onConflictTyped.Match != null)
            {
                matchColumns = GetColumns(target, onConflictTyped.Match).ToList();
            }
            else if (target.PrimaryKey.Length > 0)
            {
                matchColumns = target.PrimaryKey.Select(x => x.QuotedColumName).ToList();
            }
            else
            {
                throw new InvalidOperationException("Table has no primary key that can be used for conflict detection.");
            }

            // Validate that all match columns are available in the source subquery
            var insertedColumnNames = insertedColumns.Select(c => c.QuotedColumName).ToHashSet();
            var missingMatchColumns = matchColumns.Where(c => !insertedColumnNames.Contains(c)).ToList();
            if (missingMatchColumns.Count != 0)
            {
                throw new InvalidOperationException(
                    $"Oracle MERGE requires match columns to be present in the source data. " +
                    $"The following match columns are not available: {string.Join(", ", missingMatchColumns)}. " +
                    $"This can happen when using auto-generated primary key columns for conflict detection. " +
                    $"Use the 'Match' option to specify non-generated columns for conflict detection, " +
                    $"or set 'CopyGeneratedColumns = true' if the generated column values are provided.");
            }

            // Oracle MERGE syntax does NOT use AS for table aliases
            q.AppendLine($"MERGE INTO {target.QuotedTableName} {PseudoTableInserted}");

            q.Append("USING (SELECT ");
            q.AppendColumns(insertedColumns);
            // Oracle MERGE syntax does NOT use AS for subquery aliases
            q.Append($" FROM {source}) {PseudoTableExcluded}");
            q.AppendLine();

            // Oracle requires ON clause conditions to be wrapped in parentheses
            q.Append("ON (");
            q.AppendJoin(" AND ", matchColumns, (b, col) => b.Append($"{PseudoTableInserted}.{col} = {PseudoTableExcluded}.{col}"));
            q.AppendLine(")");

            // Oracle MERGE syntax: WHEN NOT MATCHED clause for inserts, followed by WHEN MATCHED clause for updates
            q.Append("WHEN NOT MATCHED THEN INSERT (");
            q.AppendColumns(insertedColumns);
            q.AppendLine(")");

            q.Append("VALUES (");
            q.AppendJoin(", ", insertedColumns, (b, col) => b.Append($"{PseudoTableExcluded}.{col.QuotedColumName}"));
            q.AppendLine(")");

            if (onConflictTyped.Update != null)
            {
                if (onConflictTyped is { RawWhere: not null, Where: not null })
                {
                    throw new ArgumentException("Cannot specify both RawWhere and Where in OnConflictOptions.");
                }

                q.Append("WHEN MATCHED THEN UPDATE SET ");
                // Oracle MERGE: columns in ON clause cannot be updated, so exclude match columns
                // Use insertedColumns instead of all columns because the USING subquery only contains insertedColumns
                var matchColumnSet = matchColumns.ToHashSet();
                var updateableColumns = insertedColumns.Where(c => !matchColumnSet.Contains(c.QuotedColumName)).ToList();
                // Match columns (ON clause) and primary key columns must never be overwritten by an upsert.
                var updates = ExcludeNonUpdatableColumns(
                    GetUpdates(context, target, updateableColumns, onConflictTyped.Update),
                    target,
                    matchColumns);
                if (updates.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Oracle MERGE cannot update any columns because all targeted columns are match or primary key columns. " +
                        "Specify different columns in the 'Match' option or in the 'Update' expression.");
                }
                q.AppendJoin(", ", updates);
                q.AppendLine();

                // Oracle MERGE does not support "WHEN MATCHED AND <condition> THEN"; the update
                // condition must be expressed as a trailing WHERE clause after the SET assignments.
                if (onConflictTyped.RawWhere != null || onConflictTyped.Where != null)
                {
                    q.Append("WHERE ");
                    AppendConflictCondition(q, target, context, onConflictTyped);
                }
            }
        }

        // No conflict handling
        else
        {
            q.Append($"INSERT INTO {target.QuotedTableName} (");
            q.AppendColumns(insertedColumns);
            q.AppendLine(")");
            q.Append("SELECT ");
            q.AppendColumns(insertedColumns);
            q.AppendLine();
            q.Append($"FROM {source}");
            q.AppendLine();

            if (returnedColumns.Count != 0)
            {
                q.Append("RETURNING ");
                q.AppendJoin(", ", returnedColumns, (b, col) => b.Append(col.QuotedColumName));
                q.Append(" INTO ");
                q.AppendJoin(", ", returnedColumns, (b, col) => b.Append($":{col.ColumnName}"));
                q.AppendLine();
            }
        }

        // Oracle executes these statements via ExecuteNonQuery as plain SQL, which must not end
        // with a statement terminator. A trailing ';' is only valid inside PL/SQL blocks.
        return q.ToString();
    }
    /// <summary>
    /// Removes update assignments that target match columns or primary key columns. Oracle forbids
    /// updating ON-clause (match) columns, and the primary key must never be overwritten by an upsert
    /// (overwriting a RAW(16) GUID primary key additionally raises ORA-01465).
    /// </summary>
    private List<string> ExcludeNonUpdatableColumns(
        IEnumerable<string> updateClauses,
        TableMetadata target,
        IEnumerable<string> matchColumns)
    {
        var excluded = new HashSet<string>(matchColumns);
        foreach (var pk in target.PrimaryKey)
        {
            excluded.Add(pk.QuotedColumName);
        }

        return updateClauses
            .Where(clause =>
            {
                var eq = clause.IndexOf(" = ", StringComparison.Ordinal);
                var column = eq < 0 ? clause : clause[..eq];
                return !excluded.Contains(column);
            })
            .ToList();
    }

    private const string OracleValueGenerationStrategyAnnotation = "Oracle:ValueGenerationStrategy";

    /// <summary>
    /// Adapter (Option C): maps <see cref="OnConflictOptions{T}"/> and EF Core metadata onto an
    /// <see cref="OracleUpsertPlan"/> consumed by the GTT REF CURSOR SQL generators.
    /// </summary>
    internal OracleUpsertPlan BuildUpsertPlan<T>(
        DbContext context,
        TableMetadata target,
        OracleGlobalTemporaryTableMetadata gtt,
        IReadOnlyList<ColumnMetadata> payloadColumns,
        IReadOnlyList<ColumnMetadata> insertableColumns,
        OnConflictOptions<T>? onConflict,
        IReadOnlyList<ColumnMetadata> returnedColumns) where T : class
    {
        IReadOnlyList<string> matchColumns;
        if (onConflict?.Match != null)
        {
            matchColumns = GetColumns(target, onConflict.Match).ToList();
        }
        else if (target.PrimaryKey.Length > 0)
        {
            matchColumns = target.PrimaryKey.Select(x => x.QuotedColumName).ToList();
        }
        else
        {
            throw new InvalidOperationException(
                "Table has no primary key. Specify a Match expression for conflict detection.");
        }

        var idColumn = target.PrimaryKey.Length == 1 ? target.PrimaryKey[0] : null;
        var (idStrategy, sequenceName) = DetectIdStrategy(idColumn);

        // Insertable (non store-generated) columns already exclude identity/sequence keys and computed
        // columns. A client-generated key stays in the list and is inserted straight from staging.
        var insertColumns = insertableColumns.ToList();

        var updateClauses = BuildUpdateClauses(context, target, payloadColumns, matchColumns, onConflict);
        var updateWhere = BuildUpdateWhere(context, target, onConflict);

        return new OracleUpsertPlan
        {
            Gtt = gtt,
            TargetQuotedTableName = target.QuotedTableName,
            MatchColumns = matchColumns,
            InsertColumns = insertColumns,
            UpdateClauses = updateClauses,
            UpdateWhere = updateWhere,
            IdColumn = idColumn,
            IdStrategy = idStrategy,
            SequenceName = sequenceName,
            ReturnedColumns = returnedColumns,
        };
    }

    private List<string> BuildUpdateClauses<T>(
        DbContext context,
        TableMetadata target,
        IReadOnlyList<ColumnMetadata> payloadColumns,
        IReadOnlyList<string> matchColumns,
        OnConflictOptions<T>? onConflict) where T : class
    {
        if (onConflict?.Update == null)
        {
            return [];
        }

        var matchColSet = matchColumns.ToHashSet();
        var updateableCols = payloadColumns.Where(c => !matchColSet.Contains(c.QuotedColumName)).ToList();

        return ExcludeNonUpdatableColumns(
                GetUpdates(context, target, updateableCols, onConflict.Update),
                target,
                matchColumns)
            .Select(expr => expr
                .Replace($"{PseudoTableExcluded}.", "src.")
                .Replace($"{PseudoTableInserted}.", "tgt."))
            .ToList();
    }

    private string? BuildUpdateWhere<T>(
        DbContext context,
        TableMetadata target,
        OnConflictOptions<T>? onConflict) where T : class
    {
        if (onConflict is not { } typed || (typed.RawWhere == null && typed.Where == null))
        {
            return null;
        }

        if (typed is { RawWhere: not null, Where: not null })
        {
            throw new ArgumentException("Cannot specify both RawWhere and Where in OnConflictOptions.");
        }

        var condSb = new StringBuilder();
        AppendConflictCondition(condSb, target, context, typed);
        return condSb.ToString().Trim()
            .Replace($"{PseudoTableExcluded}.", "src.")
            .Replace($"{PseudoTableInserted}.", "tgt.");
    }

    private static (OracleIdStrategy Strategy, string? SequenceName) DetectIdStrategy(ColumnMetadata? idColumn)
    {
        if (idColumn == null)
        {
            return (OracleIdStrategy.None, null);
        }

        var property = idColumn.Property;

        // Oracle EF provider records identity vs. sequence via this annotation.
        var strategy = property.FindAnnotation(OracleValueGenerationStrategyAnnotation)?.Value?.ToString();
        if (string.Equals(strategy, "IdentityColumn", StringComparison.OrdinalIgnoreCase))
        {
            return (OracleIdStrategy.Identity, null);
        }

        // A sequence-backed default (e.g. "\"MY_SEQ\".NEXTVAL") yields a Sequence strategy.
        var defaultSql = property.GetDefaultValueSql();
        if (defaultSql != null && defaultSql.Contains("NEXTVAL", StringComparison.OrdinalIgnoreCase))
        {
            return (OracleIdStrategy.Sequence, ExtractSequenceName(defaultSql));
        }

        if (string.Equals(strategy, "SequenceHiLo", StringComparison.OrdinalIgnoreCase))
        {
            return (OracleIdStrategy.Sequence, null);
        }

        // Client-side keys: explicit value generator, or Guid/string keys generated on add.
        var clrType = Nullable.GetUnderlyingType(idColumn.ClrType) ?? idColumn.ClrType;
        if (property.GetValueGeneratorFactory() != null
            || (property.ValueGenerated != Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never
                && (clrType == typeof(Guid) || clrType == typeof(string))))
        {
            return (OracleIdStrategy.ClientGenerated, null);
        }

        return (OracleIdStrategy.None, null);
    }

    private static string? ExtractSequenceName(string defaultSql)
    {
        // Handles forms like "\"SEQ\".NEXTVAL", "SCHEMA.\"SEQ\".NEXTVAL" and "SEQ.NEXTVAL".
        var nextValIndex = defaultSql.IndexOf("NEXTVAL", StringComparison.OrdinalIgnoreCase);
        if (nextValIndex <= 0)
        {
            return null;
        }

        var head = defaultSql[..nextValIndex].TrimEnd().TrimEnd('.').Trim();
        var lastDot = head.LastIndexOf('.');
        var token = lastDot >= 0 ? head[(lastDot + 1)..] : head;
        return token.Trim().Trim('"');
    }
}
