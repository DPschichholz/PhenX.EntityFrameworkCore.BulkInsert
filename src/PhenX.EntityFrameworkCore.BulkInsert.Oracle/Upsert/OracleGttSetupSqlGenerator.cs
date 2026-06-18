using System.Text;

namespace PhenX.EntityFrameworkCore.BulkInsert.Oracle.Upsert;

/// <summary>
/// Generates the DDL that creates an Oracle bulk staging Global Temporary Table (GTT).
/// <para>
/// The GTT is created once (not per batch) with <c>ON COMMIT PRESERVE ROWS</c> so the staged data
/// survives the <c>MERGE</c>/<c>INSERT</c> commit until the REF CURSOR has been read; it is then
/// cleaned up explicitly per <see cref="OracleGttColumns.BatchId"/>. The data is always session-local.
/// </para>
/// </summary>
internal sealed class OracleGttSetupSqlGenerator
{
    /// <summary>
    /// Builds the <c>CREATE GLOBAL TEMPORARY TABLE</c> statement. This does not reuse the generic
    /// <c>CreateTableCopySqlBase</c>, because the GTT needs the technical columns, the
    /// <c>GLOBAL TEMPORARY</c> keyword and the <c>ON COMMIT PRESERVE ROWS</c> clause.
    /// </summary>
    public string BuildCreateTableSql(OracleGlobalTemporaryTableMetadata gtt)
    {
        var q = new StringBuilder();

        q.Append("CREATE GLOBAL TEMPORARY TABLE ").Append(gtt.QuotedTableName).AppendLine(" (");

        for (var i = 0; i < gtt.Columns.Count; i++)
        {
            var column = gtt.Columns[i];
            var nullability = column.IsTechnical ? " NOT NULL" : " NULL";
            q.Append("  \"").Append(column.ColumnName).Append("\" ").Append(column.StoreType).Append(nullability);
            q.AppendLine(i < gtt.Columns.Count - 1 ? "," : string.Empty);
        }

        q.AppendLine(") ON COMMIT PRESERVE ROWS");

        return q.ToString();
    }

    /// <summary>
    /// Builds an idempotent PL/SQL block that creates the GTT only when it does not already exist.
    /// </summary>
    public string BuildCreateTableIfMissingSql(OracleGlobalTemporaryTableMetadata gtt)
    {
        var createSql = BuildCreateTableSql(gtt).Replace("'", "''");

        return $"""
                BEGIN
                    EXECUTE IMMEDIATE '{createSql}';
                EXCEPTION
                    WHEN OTHERS THEN
                        IF SQLCODE != -955 THEN -- ORA-00955: name is already used by an existing object
                            RAISE;
                        END IF;
                END;
                """;
    }
}

