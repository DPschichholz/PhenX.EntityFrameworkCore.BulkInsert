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

    // Column added to the temp table to store the ROWID of the corresponding inserted target row.
    private const string TargetRowIdColumn = "_target_rowid";

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
        if (onConflict != null)
        {
            throw new NotSupportedException(
                "Oracle MERGE does not support returning entities. Use ExecuteBulkInsertAsync without returning results when using conflict resolution.");
        }

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
                // corresponding row inserted into the target table.
                var quotedRowIdCol = SqlDialect.Quote(TargetRowIdColumn);
                await ExecuteAsync(sync, context, $"ALTER TABLE {tempTableName} ADD ({quotedRowIdCol} VARCHAR2(18))", ctk);

                var columns = tableInfo.GetColumns(options.CopyGeneratedColumns);
                await BulkInsert(sync, context, tableInfo, entities, tempTableName, columns, options, ctk);

                // Insert from temp into target and write the resulting target ROWIDs back into
                // the temp table row-by-row using FORALL.
                //
                // Approach:
                //   1. Snapshot the temp ROWIDs in ORDER BY ROWID order.
                //   2. INSERT ... ORDER BY ROWID with RETURNING ROWID BULK COLLECT – Oracle
                //      guarantees that the i-th returned target ROWID corresponds to the i-th
                //      source row in the same ORDER BY sequence.
                //   3. FORALL i: UPDATE temp WHERE ROWID = v_temp_rowids(i)
                //      -> stores the target ROWID into the matching temp row.
                var colList = string.Join(", ", columns.Select(c => c.QuotedColumName));
                var plsql = $"""
                             DECLARE
                               TYPE t_temp_rowid_t   IS TABLE OF ROWID     INDEX BY PLS_INTEGER;
                               TYPE t_target_rowid_t IS TABLE OF VARCHAR2(18) INDEX BY PLS_INTEGER;
                               v_temp_rowids   t_temp_rowid_t;
                               v_target_rowids t_target_rowid_t;
                             BEGIN
                               SELECT ROWID BULK COLLECT INTO v_temp_rowids
                               FROM {tempTableName} ORDER BY ROWID;

                               INSERT INTO {tableInfo.QuotedTableName} ({colList})
                               SELECT {colList} FROM {tempTableName} ORDER BY ROWID
                               RETURNING ROWIDTOCHAR(ROWID) BULK COLLECT INTO v_target_rowids;

                               FORALL i IN 1..v_target_rowids.COUNT
                                 UPDATE {tempTableName}
                                 SET {quotedRowIdCol} = v_target_rowids(i)
                                 WHERE ROWID = v_temp_rowids(i);
                             END;
                             """;
                await ExecuteAsync(sync, context, plsql, ctk);

                // Retrieve inserted target rows via the ROWIDs stored in the temp table.
                // A single subquery replaces batched IN-clause queries and avoids false positives
                // from pre-existing rows that happen to share the same column values.
                var selectSql = $"""
                                 SELECT t.* FROM {tableInfo.QuotedTableName} t
                                 WHERE ROWID IN (
                                   SELECT CHARTOROWID({quotedRowIdCol}) FROM {tempTableName}
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
