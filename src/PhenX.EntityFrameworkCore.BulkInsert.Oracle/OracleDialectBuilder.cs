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
                if (updateableColumns.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Oracle MERGE cannot update any columns because all available columns are used in the ON clause for conflict detection. " +
                        "Specify different columns in the 'Match' option or use specific columns in the 'Update' expression.");
                }
                q.AppendJoin(", ", GetUpdates(context, target, updateableColumns, onConflictTyped.Update));
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
        q.AppendLine("  TYPE t_rowid_t IS TABLE OF ROWID INDEX BY PLS_INTEGER;");
        q.AppendLine("  v_temp_rowids   t_rowid_t;");
        q.AppendLine("  v_insert_rowids t_rowid_t;");
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
            var setClauses = GetUpdates(context, target, updateableCols, onConflict.Update)
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

            q.AppendLine($"  UPDATE {target.QuotedTableName} t");
            q.AppendLine($"  SET {string.Join(", ", setClauses)}");
            q.AppendLine($"  WHERE EXISTS (SELECT 1 FROM {tempTable} s WHERE {updatedSubWhere});");
        }

        // Phase 3: Insert rows that were not matched at all (_is_matched IS NULL) and capture their
        // target ROWIDs using the same ORDER BY ROWID correspondence as the plain-insert path.
        q.AppendLine($"  SELECT ROWID BULK COLLECT INTO v_temp_rowids");
        q.AppendLine($"  FROM {tempTable} WHERE {quotedMatchedCol} IS NULL ORDER BY ROWID;");
        q.AppendLine();
        q.AppendLine($"  INSERT INTO {target.QuotedTableName} ({colList})");
        q.AppendLine($"  SELECT {colList} FROM {tempTable} WHERE {quotedMatchedCol} IS NULL ORDER BY ROWID");
        q.AppendLine($"  RETURNING ROWID BULK COLLECT INTO v_insert_rowids;");
        q.AppendLine();
        q.AppendLine($"  FORALL i IN 1..v_insert_rowids.COUNT");
        q.AppendLine($"    UPDATE {tempTable} SET {quotedRowIdCol} = v_insert_rowids(i)");
        q.AppendLine($"    WHERE ROWID = v_temp_rowids(i);");
        q.AppendLine("END;");

        return q.ToString();
    }
}
