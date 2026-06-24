using System.Text;

namespace PhenX.EntityFrameworkCore.BulkInsert.Oracle.Upsert;

/// <summary>
/// Generates the PL/SQL block that inserts staged rows into the target table and returns the
/// generated values through an <c>OPEN :rc FOR</c> REF CURSOR.
/// <para>
/// Correlation back to the originating entities is done through the technical CLIENT_ROW_ID kept in
/// the GTT. For a database sequence the ID is materialized into the staging row first, so the REF
/// CURSOR can read CLIENT_ROW_ID together with the final ID without relying on a business key.
/// </para>
/// </summary>
internal sealed class OracleInsertRefCursorSqlGenerator
{
    public string Build(OracleUpsertPlan plan)
    {
        var gtt = plan.Gtt.QuotedTableName;
        var target = plan.TargetQuotedTableName;

        if (plan.IdStrategy == OracleIdStrategy.Identity)
        {
            throw new NotSupportedException(
                "Returning entities for an identity-generated key via the GTT pipeline is not supported. " +
                "Use a sequence or a client-side value generator for the key.");
        }

        var q = new StringBuilder();
        q.AppendLine("BEGIN");

        var insertColumns = plan.InsertColumns.Select(c => c.QuotedColumName).ToList();

        // Sequence: materialize the next value into the staging row first, so it can be both inserted
        // into the target and read back per CLIENT_ROW_ID from the same GTT row.
        if (plan.IdStrategy == OracleIdStrategy.Sequence)
        {
            if (plan.IdColumn == null || string.IsNullOrWhiteSpace(plan.SequenceName))
            {
                throw new InvalidOperationException("Sequence ID strategy requires an ID column and a sequence name.");
            }

            q.Append("  UPDATE ").Append(gtt).Append(" SET ").Append(plan.IdColumn.QuotedColumName)
                .Append(" = \"").Append(plan.SequenceName).Append("\".NEXTVAL WHERE \"")
                .Append(OracleGttColumns.BatchId).Append("\" = :").Append(OracleUpsertParameters.BatchId)
                .Append(" AND ").Append(plan.IdColumn.QuotedColumName).AppendLine(" IS NULL;");

            if (!insertColumns.Contains(plan.IdColumn.QuotedColumName))
            {
                insertColumns.Insert(0, plan.IdColumn.QuotedColumName);
            }
        }

        q.Append("  INSERT INTO ").Append(target).Append(" (");
        q.AppendJoin(", ", insertColumns).AppendLine(")");
        q.Append("  SELECT ");
        q.AppendJoin(", ", insertColumns).AppendLine();
        q.Append("  FROM ").Append(gtt).Append(" WHERE \"").Append(OracleGttColumns.BatchId)
            .Append("\" = :").Append(OracleUpsertParameters.BatchId).AppendLine(";");
        q.AppendLine();

        // REF CURSOR straight from the staging row: CLIENT_ROW_ID + the (now known) ID + extras.
        q.Append("  OPEN :").Append(OracleUpsertParameters.RefCursor).AppendLine(" FOR");
        q.Append("    SELECT \"").Append(OracleGttColumns.ClientRowId).Append("\" AS \"")
            .Append(OracleUpsertParameters.ClientRowIdAlias).Append('"');

        if (plan.IdColumn != null)
        {
            q.Append(", ").Append(plan.IdColumn.QuotedColumName);
        }

        foreach (var returned in plan.ReturnedColumns)
        {
            q.Append(", ").Append(returned.QuotedColumName);
        }

        q.AppendLine();
        q.Append("    FROM ").Append(gtt).Append(" WHERE \"").Append(OracleGttColumns.BatchId)
            .Append("\" = :").Append(OracleUpsertParameters.BatchId).AppendLine(";");

        q.AppendLine("END;");
        return q.ToString();
    }
}

