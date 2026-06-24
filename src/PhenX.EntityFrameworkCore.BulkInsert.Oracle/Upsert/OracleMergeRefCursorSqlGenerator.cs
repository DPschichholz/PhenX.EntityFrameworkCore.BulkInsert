using System.Text;

using PhenX.EntityFrameworkCore.BulkInsert.Metadata;

namespace PhenX.EntityFrameworkCore.BulkInsert.Oracle.Upsert;

/// <summary>
/// Generates the PL/SQL block that upserts staged rows into the target table via <c>MERGE</c> and
/// returns the generated values through an <c>OPEN :rc FOR</c> REF CURSOR.
/// <para>Does not use <c>MERGE ... RETURNING</c> (not viable on Oracle 21c for this scenario).</para>
/// </summary>
internal sealed class OracleMergeRefCursorSqlGenerator
{
    public string Build(OracleUpsertPlan plan)
    {
        if (plan.MatchColumns.Count == 0)
        {
            throw new InvalidOperationException("An Oracle upsert requires at least one match column.");
        }

        var gtt = plan.Gtt.QuotedTableName;
        var target = plan.TargetQuotedTableName;

        var q = new StringBuilder();
        q.AppendLine("BEGIN");

        // MERGE source: select every staged payload column for the current batch.
        q.Append("  MERGE INTO ").Append(target).AppendLine(" tgt");
        q.AppendLine("  USING (");
        q.Append("    SELECT ");
        q.AppendJoin(", ", plan.Gtt.PayloadColumns.Select(c => $"\"{c.ColumnName}\""));
        q.AppendLine();
        q.Append("    FROM ").Append(gtt).Append(" WHERE \"").Append(OracleGttColumns.BatchId)
            .Append("\" = :").Append(OracleUpsertParameters.BatchId).AppendLine()
            .AppendLine("  ) src");
        q.Append("  ON (").Append(BuildOnCondition(plan.MatchColumns)).AppendLine(")");

        // WHEN MATCHED (optional): Oracle rejects an empty UPDATE SET, so skip the clause entirely
        // when there is nothing to update. Existing IDs are still returned via the REF CURSOR join.
        if (plan.UpdateClauses.Count > 0)
        {
            q.AppendLine("  WHEN MATCHED THEN UPDATE SET");
            q.Append("    ").AppendJoin(", ", plan.UpdateClauses).AppendLine();

            if (!string.IsNullOrWhiteSpace(plan.UpdateWhere))
            {
                q.Append("    WHERE ").AppendLine(plan.UpdateWhere);
            }
        }

        AppendNotMatchedInsert(q, plan);
        q.Length -= Environment.NewLine.Length; // drop trailing newline before the terminator
        q.AppendLine(";");
        q.AppendLine();

        OracleRefCursorSelectBuilder.AppendOpenRefCursor(q, plan);

        q.AppendLine("END;");
        return q.ToString();
    }

    private static void AppendNotMatchedInsert(StringBuilder q, OracleUpsertPlan plan)
    {
        var insertColumns = new List<string>();
        var insertValues = new List<string>();

        // Sequence-generated ID: emit SEQ.NEXTVAL; identity: omit the ID entirely; client-generated /
        // none: the ID (if any) is part of InsertColumns and taken straight from the source.
        if (plan.IdStrategy == OracleIdStrategy.Sequence)
        {
            if (plan.IdColumn == null)
            {
                throw new InvalidOperationException("Sequence ID strategy requires an ID column.");
            }

            if (string.IsNullOrWhiteSpace(plan.SequenceName))
            {
                throw new InvalidOperationException("Sequence ID strategy requires a sequence name.");
            }

            insertColumns.Add(plan.IdColumn.QuotedColumName);
            insertValues.Add($"\"{plan.SequenceName}\".NEXTVAL");
        }

        foreach (var column in plan.InsertColumns)
        {
            insertColumns.Add(column.QuotedColumName);
            insertValues.Add($"src.{column.QuotedColumName}");
        }

        q.AppendLine("  WHEN NOT MATCHED THEN INSERT (");
        q.Append("    ").AppendJoin(", ", insertColumns).AppendLine();
        q.AppendLine("  )");
        q.AppendLine("  VALUES (");
        q.Append("    ").AppendJoin(", ", insertValues).AppendLine();
        q.AppendLine("  )");
    }

    private static string BuildOnCondition(IReadOnlyList<string> matchColumns)
    {
        return string.Join(" AND ", matchColumns.Select(c => $"tgt.{c} = src.{c}"));
    }
}

