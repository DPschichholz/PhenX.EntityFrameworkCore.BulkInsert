using System.Text;

using Microsoft.EntityFrameworkCore;

using PhenX.EntityFrameworkCore.BulkInsert.Dialect;
using PhenX.EntityFrameworkCore.BulkInsert.Metadata;
using PhenX.EntityFrameworkCore.BulkInsert.Options;

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
                q.Append("WHEN MATCHED ");

                if (onConflictTyped.RawWhere != null || onConflictTyped.Where != null)
                {
                    if (onConflictTyped is { RawWhere: not null, Where: not null })
                    {
                        throw new ArgumentException("Cannot specify both RawWhere and Where in OnConflictOptions.");
                    }

                    q.Append("AND ");
                    AppendConflictCondition(q, target, context, onConflictTyped);
                }

                q.AppendLine("THEN UPDATE SET ");
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
    /// Builds an anonymous PL/SQL block that performs an UPSERT from <paramref name="tempTable"/> into the
    /// target table and writes the target ROWID of every affected row (updated or newly inserted) back into
    /// <paramref name="quotedRowIdCol"/> in the temp table.
    /// <para>
    /// <paramref name="quotedMatchedCol"/> is set to 1 for every temp row that has a matching target row,
    /// regardless of whether the optional WHERE condition is satisfied. This allows Phase 3 to distinguish
    /// "not matched → INSERT" from "matched but WHERE=false → DoNothing".
    /// </para>
    /// </summary>
    internal string BuildUpsertReturnPLSQL<T>(
        DbContext context,
        TableMetadata target,
        string tempTable,
        string quotedRowIdCol,
        string quotedMatchedCol,
        IReadOnlyList<ColumnMetadata> insertedColumns,
        OnConflictOptions<T> onConflict) where T : class
    {
        // Resolve match columns from expression or primary key
        IReadOnlyList<string> matchColumns;
        if (onConflict.Match != null)
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

        var insertedColSet = insertedColumns.Select(c => c.QuotedColumName).ToHashSet();
        var missingCols = matchColumns.Where(c => !insertedColSet.Contains(c)).ToList();
        if (missingCols.Count != 0)
        {
            throw new InvalidOperationException(
                $"Oracle UPSERT requires match columns to be present in the source data. " +
                $"Missing: {string.Join(", ", missingCols)}. " +
                $"Use the 'Match' option to specify non-generated columns, " +
                $"or set 'CopyGeneratedColumns = true' if the generated values are provided.");
        }

        var matchCondition = string.Join(" AND ", matchColumns.Select(c => $"s.{c} = t.{c}"));
        var colList = string.Join(", ", insertedColumns.Select(c => c.QuotedColumName));

        // Translate the optional WHERE condition to t./s. aliases used in correlated SQL
        string whereFragment = "";
        if (onConflict.RawWhere != null || onConflict.Where != null)
        {
            if (onConflict is { RawWhere: not null, Where: not null })
            {
                throw new ArgumentException("Cannot specify both RawWhere and Where in OnConflictOptions.");
            }

            var condSb = new StringBuilder();
            AppendConflictCondition(condSb, target, context, onConflict);
            whereFragment = condSb.ToString().Trim()
                .Replace($"{PseudoTableExcluded}.", "s.")
                .Replace($"{PseudoTableInserted}.", "t.");
        }

        // Phase 1: _target_rowid receives the target ROWID only when the WHERE condition is satisfied
        // (or when there is no WHERE condition). This means _target_rowid IS NOT NULL ↔ "row will be returned".
        var rowidSubquery = string.IsNullOrEmpty(whereFragment)
            ? $"SELECT t.ROWID FROM {target.QuotedTableName} t WHERE {matchCondition}"
            : $"SELECT t.ROWID FROM {target.QuotedTableName} t WHERE {matchCondition} AND {whereFragment}";

        // Phase 2: correlated subquery WHERE reuses the same match + _target_rowid check
        var updatedSubWhere = $"{matchCondition} AND s.{quotedRowIdCol} IS NOT NULL";

        var q = new StringBuilder();
        q.AppendLine("DECLARE");
        q.AppendLine("  TYPE t_rowid_t IS TABLE OF UROWID INDEX BY PLS_INTEGER;");
        q.AppendLine("  v_temp_rowids   t_rowid_t;");
        q.AppendLine("  v_insert_rowids t_rowid_t;");
        // One PL/SQL collection per inserted column, anchored to the temp table column type, so the
        // not-matched rows can be inserted via FORALL (see Phase 3 for why FORALL is required).
        for (var i = 0; i < insertedColumns.Count; i++)
        {
            q.AppendLine($"  TYPE t_col{i}_t IS TABLE OF {tempTable}.{insertedColumns[i].QuotedColumName}%TYPE INDEX BY PLS_INTEGER;");
            q.AppendLine($"  v_col{i} t_col{i}_t;");
        }
        q.AppendLine("BEGIN");

        // Phase 1: For every temp row that matches a target row:
        //   - set _is_matched = 1 (prevents Phase 3 from re-inserting it)
        //   - set _target_rowid to the target ROWID when WHERE holds (NULL otherwise)
        q.AppendLine($"  UPDATE {tempTable} s SET");
        q.AppendLine($"    {quotedMatchedCol} = 1,");
        q.AppendLine($"    {quotedRowIdCol} = ({rowidSubquery})");
        q.AppendLine($"  WHERE EXISTS (SELECT 1 FROM {target.QuotedTableName} t WHERE {matchCondition});");

        // Phase 2: Apply the update expression to rows where _target_rowid was set (WHERE held)
        if (onConflict.Update != null)
        {
            var matchColSet = matchColumns.ToHashSet();
            var updateableCols = insertedColumns.Where(c => !matchColSet.Contains(c.QuotedColumName)).ToList();
            if (updateableCols.Count == 0)
            {
                throw new InvalidOperationException(
                    "Oracle UPSERT cannot update any columns because all source columns are used in the match condition. " +
                    "Specify different columns in the 'Match' option or use specific columns in the 'Update' expression.");
            }

            // GetUpdates produces "col = EXCLUDED.expr"-style strings (EXCLUDED = source, INSERTED = target).
            // Rewrite each assignment as a correlated subquery so that both t (target outer row) and
            // s (temp inner alias) are accessible, which is required for expressions that reference
            // columns from both tables (e.g. inserted.Price + excluded.Price).
            // Match columns and primary key columns are excluded: the primary key must never be
            // overwritten by an upsert (and on Oracle a RAW(16) PK assignment can raise ORA-01465).
            var setClauses = ExcludeNonUpdatableColumns(
                    GetUpdates(context, target, updateableCols, onConflict.Update),
                    target,
                    matchColumns)
                .Select(expr =>
                {
                    var adapted = expr
                        .Replace($"{PseudoTableExcluded}.", "s.")
                        .Replace($"{PseudoTableInserted}.", "t.");
                    var eq = adapted.IndexOf(" = ", StringComparison.Ordinal);
                    var col = adapted[..eq];
                    var val = adapted[(eq + 3)..];
                    return $"{col} = (SELECT {val} FROM {tempTable} s WHERE {updatedSubWhere})";
                })
                .ToList();

            // When the only requested updates target match/primary key columns, there is nothing left
            // to update. The matched rows still have their _target_rowid set in Phase 1, so they are
            // returned; we simply skip the (otherwise invalid empty) UPDATE statement.
            if (setClauses.Count != 0)
            {
                q.AppendLine($"  UPDATE {target.QuotedTableName} t");
                q.AppendLine($"  SET {string.Join(", ", setClauses)}");
                q.AppendLine($"  WHERE EXISTS (SELECT 1 FROM {tempTable} s WHERE {updatedSubWhere});");
            }
        }

        // Phase 3: Insert rows that were not matched at all (_is_matched IS NULL) and capture their
        // target ROWIDs. Oracle does not support "RETURNING ... BULK COLLECT" on "INSERT ... SELECT"
        // (ORA-03049), so the not-matched rows are bulk-collected into PL/SQL collections and inserted
        // via FORALL, which returns the new target ROWIDs in the same iteration order.
        var selectColList = string.Join(", ", insertedColumns.Select(c => c.QuotedColumName));
        var intoColVars = string.Join(", ", insertedColumns.Select((_, i) => $"v_col{i}"));
        var valuesList = string.Join(", ", insertedColumns.Select((_, i) => $"v_col{i}(i)"));

        q.AppendLine($"  SELECT ROWID, {selectColList}");
        q.AppendLine($"  BULK COLLECT INTO v_temp_rowids, {intoColVars}");
        q.AppendLine($"  FROM {tempTable} WHERE {quotedMatchedCol} IS NULL ORDER BY ROWID;");
        q.AppendLine();
        q.AppendLine($"  FORALL i IN 1..v_temp_rowids.COUNT");
        q.AppendLine($"    INSERT INTO {target.QuotedTableName} ({colList})");
        q.AppendLine($"    VALUES ({valuesList})");
        q.AppendLine($"    RETURNING ROWID BULK COLLECT INTO v_insert_rowids;");
        q.AppendLine();
        q.AppendLine($"  FORALL i IN 1..v_insert_rowids.COUNT");
        q.AppendLine($"    UPDATE {tempTable} SET {quotedRowIdCol} = v_insert_rowids(i)");
        q.AppendLine($"    WHERE ROWID = v_temp_rowids(i);");
        q.AppendLine("END;");

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
}
