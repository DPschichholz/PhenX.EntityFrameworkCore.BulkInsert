using System.Data;
using System.Runtime.CompilerServices;

using JetBrains.Annotations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

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
            string[] rowids;
            try
            {
                var columns = tableInfo.GetColumns(options.CopyGeneratedColumns);
                await BulkInsert(sync, context, tableInfo, entities, tempTableName, columns, options, ctk);
                rowids = await InsertFromTempAndCollectRowIds(sync, context, tableInfo, tempTableName, columns, ctk);
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

            const int rowidBatchSize = 1000;
            for (var i = 0; i < rowids.Length; i += rowidBatchSize)
            {
                var inClause = string.Join(", ", rowids.Skip(i).Take(rowidBatchSize).Select(r => $"'{r}'"));
                var sql = $"SELECT * FROM {tableInfo.QuotedTableName} WHERE ROWID IN ({inClause})";
                await foreach (var item in context.Set<T>().FromSqlRaw(sql).AsAsyncEnumerable().WithCancellation(ctk))
                {
                    yield return item;
                }
            }

            await connection.Commit(sync, ctk);
        }
        finally
        {
            await connection.Close(sync, ctk);
        }
    }

    private async Task<string[]> InsertFromTempAndCollectRowIds(
        bool sync,
        DbContext context,
        TableMetadata tableInfo,
        string tempTableName,
        IReadOnlyList<ColumnMetadata> columns,
        CancellationToken ctk)
    {
        await using var countCmd = context.Database.GetDbConnection().CreateCommand();
        countCmd.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        countCmd.CommandText = $"SELECT COUNT(*) FROM {tempTableName}";

        int rowCount;
        if (sync)
        {
            rowCount = Convert.ToInt32(countCmd.ExecuteScalar());
        }
        else
        {
            rowCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ctk));
        }

        if (rowCount == 0)
        {
            return [];
        }

        var colList = string.Join(", ", columns.Select(c => c.QuotedColumName));
        var plsql = $"""
                     DECLARE
                       TYPE t_rowid_list IS TABLE OF VARCHAR2(18) INDEX BY PLS_INTEGER;
                       v_rowids t_rowid_list;
                     BEGIN
                       INSERT INTO {tableInfo.QuotedTableName} ({colList})
                       SELECT {colList} FROM {tempTableName}
                       RETURNING ROWID BULK COLLECT INTO v_rowids;
                       :out_rowids := v_rowids;
                     END;
                     """;

        await using var cmd = (OracleCommand)context.Database.GetDbConnection().CreateCommand();
        cmd.Transaction = (OracleTransaction)context.Database.CurrentTransaction!.GetDbTransaction();
        cmd.CommandText = plsql;

        var rowidParam = new OracleParameter
        {
            ParameterName = "out_rowids",
            OracleDbType = OracleDbType.Varchar2,
            Direction = ParameterDirection.Output,
            CollectionType = OracleCollectionType.PLSQLAssociativeArray,
            Size = rowCount,
            ArrayBindSize = Enumerable.Repeat(18, rowCount).ToArray(),
        };
        cmd.Parameters.Add(rowidParam);

        if (sync)
        {
            cmd.ExecuteNonQuery();
        }
        else
        {
            await cmd.ExecuteNonQueryAsync(ctk);
        }

        return ((OracleString[])rowidParam.Value)
            .Where(r => !r.IsNull)
            .Select(r => r.Value)
            .ToArray();
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
