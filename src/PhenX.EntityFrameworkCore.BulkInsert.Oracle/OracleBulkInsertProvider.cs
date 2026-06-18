using System.Runtime.CompilerServices;

using JetBrains.Annotations;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Oracle.ManagedDataAccess.Client;

using PhenX.EntityFrameworkCore.BulkInsert.Metadata;
using PhenX.EntityFrameworkCore.BulkInsert.Options;
using PhenX.EntityFrameworkCore.BulkInsert.Oracle.Upsert;

namespace PhenX.EntityFrameworkCore.BulkInsert.Oracle;

[UsedImplicitly]
internal class OracleBulkInsertProvider(ILoggerFactory? loggerFactory) : BulkInsertProviderBase<OracleDialectBuilder, OracleBulkInsertOptions>(loggerFactory)
{
    private readonly OracleBatchUpsertService _upsertService = new();

    /// <inheritdoc />
    protected override string BulkInsertId => "ROWID";

    /// <inheritdoc />
    protected override string AddTableCopyBulkInsertId => ""; // No need to add an ID column in Oracle

    protected override OracleBulkInsertOptions CreateDefaultOptions() => new()
    {
        BatchSize = 50_000,
    };

    /// <summary>
    /// Returns inserted/updated entities via the GTT MERGE/INSERT + REF CURSOR pipeline, writing the
    /// generated values back onto the originating instances by CLIENT_ROW_ID before yielding them.
    /// </summary>
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

        ValidateOptions(options);

        var list = entities as IReadOnlyList<T> ?? entities.ToList();

        await _upsertService.RunAsync(
            sync, context, SqlDialect, tableInfo, list, options, onConflict, returnEntities: true, ctk);

        foreach (var item in list)
        {
            yield return item;
        }
    }

    /// <summary>
    /// Performs a bulk insert (direct-path) or a bulk upsert (GTT MERGE + REF CURSOR) without
    /// returning entities.
    /// </summary>
    protected override async Task BulkInsert<T>(
        bool sync,
        DbContext context,
        TableMetadata tableInfo,
        IEnumerable<T> entities,
        OracleBulkInsertOptions options,
        OnConflictOptions<T>? onConflict,
        CancellationToken ctk) where T : class
    {
        ValidateOptions(options);

        // A plain insert (no conflict resolution) keeps using the direct-path bulk copy into the
        // target table; only the upsert path goes through the GTT MERGE/REF CURSOR pipeline.
        if (onConflict == null)
        {
            await base.BulkInsert(sync, context, tableInfo, entities, options, onConflict, ctk);
            return;
        }

        using var activity = Telemetry.ActivitySource.StartActivity("BulkUpsert");
        activity?.AddTag("tableName", tableInfo.TableName);
        activity?.AddTag("synchronous", sync);

        var list = entities as IReadOnlyList<T> ?? entities.ToList();

        await _upsertService.RunAsync(
            sync, context, SqlDialect, tableInfo, list, options, onConflict, returnEntities: false, ctk);
    }

    private static void ValidateOptions(OracleBulkInsertOptions options)
    {
        // MoveRows deletes from the source after copying; it has no meaning for the session-local
        // staging GTT and is unsupported for Oracle.
        if (options.MoveRows)
        {
            throw new NotSupportedException(
                "MoveRows is not supported for Oracle. The bulk pipeline stages rows in a Global Temporary Table.");
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

        // OracleBulkCopy only evaluates cancellation in the OracleRowsCopied event, which fires
        // every NotifyAfter rows. A value must always be set (even without progress callback) so
        // that cancellation is observed promptly instead of only at the end of the whole load.
        bulkCopy.NotifyAfter = options.NotifyProgressAfter ?? options.BatchSize;

        var hasProgress = options is { NotifyProgressAfter: not null, OnProgress: not null };

        bulkCopy.OracleRowsCopied += (_, e) =>
        {
            if (hasProgress)
            {
                options.OnProgress!(e.RowsCopied);
            }

            if (ctk.IsCancellationRequested)
            {
                e.Abort = true;
            }
        };

        foreach (var column in columns)
        {
            bulkCopy.ColumnMappings.Add(column.PropertyName, column.QuotedColumName);
        }

        var dataReader = new EnumerableDataReader<T>(entities, columns, options);

        try
        {
            bulkCopy.WriteToServer(dataReader);
        }
        catch
        {
            // OracleBulkCopy runs a direct-path load that does NOT enlist in the EF transaction and
            // holds an exclusive (TM) lock on the destination table. When the load is aborted (via
            // e.Abort on cancellation) ODP.NET throws; surface a clean cancellation in that case.
            if (ctk.IsCancellationRequested)
            {
                throw new OperationCanceledException(ctk);
            }

            throw;
        }
        finally
        {
            // Explicitly close the bulk copy so the direct-path stream is finalized and the table
            // lock / server session is released before the object is disposed. Without this an
            // aborted load can leave a lock or session hanging on the server.
            bulkCopy.Close();
        }

        return Task.CompletedTask;
    }
}

