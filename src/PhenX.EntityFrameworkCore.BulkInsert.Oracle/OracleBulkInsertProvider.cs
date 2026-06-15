using System.Runtime.CompilerServices;

using JetBrains.Annotations;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Oracle.ManagedDataAccess.Client;

using PhenX.EntityFrameworkCore.BulkInsert.Extensions;
using PhenX.EntityFrameworkCore.BulkInsert.Metadata;
using PhenX.EntityFrameworkCore.BulkInsert.Options;

namespace PhenX.EntityFrameworkCore.BulkInsert.Oracle;

[UsedImplicitly]
internal class OracleBulkInsertProvider(ILoggerFactory? loggerFactory) : BulkInsertProviderBase<OracleDialectBuilder, OracleBulkInsertOptions>(loggerFactory)
{
    /// <inheritdoc />
    protected override string BulkInsertId => "ROWID";

    /// <inheritdoc />
    protected override string AddTableCopyBulkInsertId => ""; // No need to add an ID column in Oracle

    /// <inheritdoc />
    /// <summary>
    /// The temporary table name is generated with a random 8-character suffix to ensure uniqueness, and is limited to less than 30 characters,
    /// because Oracle prior to 12.2 has a limit of 30 characters for identifiers.
    /// </summary>
    protected override string GetTempTableName(string tableName) => $"#temp_bulk_insert_{Helpers.RandomString(8)}";

    protected override OracleBulkInsertOptions CreateDefaultOptions() => new()
    {
        BatchSize = 50_000,
    };

    // Column added to the temp table to store the ROWID of the corresponding inserted/updated target row.
    private const string TargetRowIdColumn = "_target_rowid";

    // Column added to the temp table (only for UPSERT) to mark rows that matched an existing target row.
    // Distinguishes "not matched → INSERT" from "matched but WHERE=false → DoNothing (skip INSERT)".
    private const string IsMatchedColumn = "_is_matched";

    /// <inheritdoc />
    protected override async IAsyncEnumerable<T> BulkInsertReturnEntities<T>(
        bool sync,
        DbContext context,
        TableMetadata tableInfo,
        IEnumerable<T> entities,
        OracleBulkInsertOptions options,
        OnConflictOptions<T>? onConflict,
        [EnumeratorCancellation] CancellationToken ctk) where T : class
    {
        using var activity = Telemetry.ActivitySource.StartActivity("BulkInsertReturnEntities");
        activity?.AddTag("tableName", tableInfo.TableName);
        activity?.AddTag("synchronous", sync);

        var connection = await context.GetConnection(sync, ctk);
        try
        {
            var tempTableName = await CreateTableCopyAsync<T>(sync, context, options, tableInfo, ctk);
            try
            {
                // Extend the temp table with a column that will hold the ROWID of the
                // corresponding row inserted/updated in the target table.
                // For UPSERT, also add _is_matched so Phase 3 can skip re-inserting matched rows.
                var quotedRowIdCol = SqlDialect.Quote(TargetRowIdColumn);
                var alterSql = onConflict != null
                    ? $"ALTER TABLE {tempTableName} ADD ({quotedRowIdCol} ROWID, {SqlDialect.Quote(IsMatchedColumn)} NUMBER(1))"
                    : $"ALTER TABLE {tempTableName} ADD ({quotedRowIdCol} ROWID)";
                await ExecuteAsync(sync, context, alterSql, ctk);

                var columns = tableInfo.GetColumns(options.CopyGeneratedColumns);
                await BulkInsert(sync, context, tableInfo, entities, tempTableName, columns, options, ctk);

                var plsql = onConflict == null
                    ? BuildInsertReturnPLSQL(tableInfo, tempTableName, quotedRowIdCol, columns)
                    : SqlDialect.BuildUpsertReturnPLSQL(
                        context, tableInfo, tempTableName,
                        quotedRowIdCol, SqlDialect.Quote(IsMatchedColumn),
                        columns, onConflict);
                await ExecuteAsync(sync, context, plsql, ctk);

                // Retrieve all affected rows (inserted or updated) via their ROWIDs stored in the temp table.
                var selectSql = $"""
                                 SELECT t.* FROM {tableInfo.QuotedTableName} t
                                 WHERE ROWID IN (
                                   SELECT {quotedRowIdCol} FROM {tempTableName}
                                   WHERE {quotedRowIdCol} IS NOT NULL
                                 )
                                 """;
                await foreach (var item in context.Set<T>().FromSqlRaw(selectSql).AsAsyncEnumerable().WithCancellation(ctk))
                {
                    yield return item;
                }
            }
            finally
            {
                try
                {
                    await DropTempTableAsync(sync, context, tempTableName);
                }
                catch
                {
                    // Drop is best-effort; ignore errors
                }
            }

            await connection.Commit(sync, ctk);
        }
        finally
        {
            await connection.Close(sync, ctk);
        }
    }

    // Builds the PL/SQL block for plain INSERT (no conflict resolution).
    //
    // Oracle does NOT support "RETURNING ... BULK COLLECT" on an "INSERT ... SELECT" statement
    // (ORA-03049). RETURNING BULK COLLECT is only valid with FORALL (collection-bound) DML.
    // The source rows are therefore bulk-collected from the temp table into PL/SQL collections
    // and inserted via FORALL, which lets us capture the newly inserted target ROWIDs in the
    // same iteration order and map them back onto the originating temp rows.
    private static string BuildInsertReturnPLSQL(
        TableMetadata tableInfo,
        string tempTableName,
        string quotedRowIdCol,
        IReadOnlyList<ColumnMetadata> columns)
    {
        var colList = string.Join(", ", columns.Select(c => c.QuotedColumName));
        var colDeclarations = string.Join("\n", columns.Select((c, i) =>
            $"  TYPE t_col{i}_t IS TABLE OF {tempTableName}.{c.QuotedColumName}%TYPE INDEX BY PLS_INTEGER;\n" +
            $"  v_col{i} t_col{i}_t;"));
        var selectColList = string.Join(", ", columns.Select(c => c.QuotedColumName));
        var intoColVars = string.Join(", ", columns.Select((_, i) => $"v_col{i}"));
        var valuesList = string.Join(", ", columns.Select((_, i) => $"v_col{i}(i)"));

        return $"""
                DECLARE
                  TYPE t_rowid_t IS TABLE OF UROWID INDEX BY PLS_INTEGER;
                  v_temp_rowids   t_rowid_t;
                  v_target_rowids t_rowid_t;
                {colDeclarations}
                BEGIN
                  SELECT ROWID, {selectColList}
                  BULK COLLECT INTO v_temp_rowids, {intoColVars}
                  FROM {tempTableName} ORDER BY ROWID;

                  FORALL i IN 1..v_temp_rowids.COUNT
                    INSERT INTO {tableInfo.QuotedTableName} ({colList})
                    VALUES ({valuesList})
                    RETURNING ROWID BULK COLLECT INTO v_target_rowids;

                  FORALL i IN 1..v_target_rowids.COUNT
                    UPDATE {tempTableName}
                    SET {quotedRowIdCol} = v_target_rowids(i)
                    WHERE ROWID = v_temp_rowids(i);
                END;
                """;
    }

    /// <inheritdoc />
    protected override Task BulkInsert<T>(
        bool sync,
        DbContext context,
        TableMetadata tableInfo,
        IEnumerable<T> entities,
        string tableName,
        IReadOnlyList<ColumnMetadata> columns,
        OracleBulkInsertOptions options,
        CancellationToken ctk)
    {
        var connection = (OracleConnection)context.Database.GetDbConnection();

        using var bulkCopy = new OracleBulkCopy(connection, options.CopyOptions);

        // OracleBulkCopy's direct-path load cannot handle schema-qualified table names.
        // When a schema is configured (e.g. via HasDefaultSchema()), the QuotedTableName
        // includes a schema prefix (e.g. "SchemaX"."TableA"), which ODP.NET double-applies,
        // producing SchemaX.SchemaX.TableA (ORA-39831). Use DestinationSchemaName explicitly.
        if (tableName == tableInfo.QuotedTableName && tableInfo.Schema != null)
        {
            bulkCopy.DestinationTableName = SqlDialect.Quote(tableInfo.TableName);
            bulkCopy.DestinationSchemaName = tableInfo.Schema;
        }
        else
        {
            bulkCopy.DestinationTableName = tableName;
        }

        bulkCopy.BatchSize = options.BatchSize;
        bulkCopy.BulkCopyTimeout = options.GetCopyTimeoutInSeconds();

        // Handle progress notifications
        if (options is { NotifyProgressAfter: not null, OnProgress: not null })
        {
            bulkCopy.NotifyAfter = options.NotifyProgressAfter.Value;

            bulkCopy.OracleRowsCopied += (sender, e) =>
            {
                options.OnProgress(e.RowsCopied);

                if (ctk.IsCancellationRequested)
                {
                    e.Abort = true;
                }
            };
        }

        // If no progress notification is set, we still need to handle cancellation.
        else
        {
            bulkCopy.OracleRowsCopied += (sender, e) =>
            {
                if (ctk.IsCancellationRequested)
                {
                    e.Abort = true;
                }
            };
        }

        foreach (var column in columns)
        {
            bulkCopy.ColumnMappings.Add(column.PropertyName, column.QuotedColumName);
        }

        var dataReader = new EnumerableDataReader<T>(entities, columns, options);

        bulkCopy.WriteToServer(dataReader);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task DropTempTableAsync(bool sync, DbContext dbContext, string tableName)
    {
        var commandText = $"""
                           BEGIN
                               EXECUTE IMMEDIATE 'DROP TABLE {tableName}';
                           EXCEPTION
                               WHEN OTHERS THEN
                                   IF SQLCODE != -942 THEN -- ORA-00942: table or view does not exist
                                       RAISE;
                                   END IF;
                           END;
                           """;

        await ExecuteAsync(sync, dbContext, commandText, CancellationToken.None);
    }
}
