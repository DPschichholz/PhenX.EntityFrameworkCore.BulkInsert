using System.Text;

using PhenX.EntityFrameworkCore.BulkInsert.Metadata;

namespace PhenX.EntityFrameworkCore.BulkInsert.Oracle.Upsert;

/// <summary>
/// Generates the REF CURSOR SELECT that returns CLIENT_ROW_ID, the generated ID and any optional
/// returned columns, joining the staging GTT back to the target table on the match columns.
/// <para>
/// Oracle 21c has no usable <c>MERGE ... RETURNING</c> for this scenario, so the generated values are
/// read back through this correlated join keyed by the technical CLIENT_ROW_ID.
/// </para>
/// </summary>
internal static class OracleRefCursorSelectBuilder
{
    public static void AppendOpenRefCursor(StringBuilder q, OracleUpsertPlan plan)
    {
        var gtt = plan.Gtt.QuotedTableName;
        var target = plan.TargetQuotedTableName;

        q.Append("  OPEN :").Append(OracleUpsertParameters.RefCursor).AppendLine(" FOR");
        q.Append("    SELECT src.\"").Append(OracleGttColumns.ClientRowId).Append("\" AS \"")
            .Append(OracleUpsertParameters.ClientRowIdAlias).Append('"');

        if (plan.IdColumn != null)
        {
            q.Append(", tgt.").Append(plan.IdColumn.QuotedColumName);
        }

        foreach (var returned in plan.ReturnedColumns)
        {
            q.Append(", tgt.").Append(returned.QuotedColumName);
        }

        q.AppendLine();
        q.Append("    FROM ").Append(gtt).AppendLine(" src");
        q.Append("    JOIN ").Append(target).AppendLine(" tgt");
        q.Append("      ON ").AppendLine(BuildMatchCondition(plan.MatchColumns));
        q.Append("    WHERE src.\"").Append(OracleGttColumns.BatchId).Append("\" = :")
            .Append(OracleUpsertParameters.BatchId).AppendLine(";");
    }

    public static string BuildMatchCondition(IReadOnlyList<string> matchColumns)
    {
        return string.Join(" AND ", matchColumns.Select(c => $"tgt.{c} = src.{c}"));
    }
}

