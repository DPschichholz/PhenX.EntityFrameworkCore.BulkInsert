using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Oracle.ManagedDataAccess.Client;

using PhenX.EntityFrameworkCore.BulkInsert.Extensions;
using PhenX.EntityFrameworkCore.BulkInsert.Metadata;
using PhenX.EntityFrameworkCore.BulkInsert.Options;

namespace PhenX.EntityFrameworkCore.BulkInsert.Oracle.Upsert;

/// <summary>
/// Orchestrates the Oracle 21c GTT bulk pipeline:
/// <code>
/// value generators → CLIENT_ROW_ID → BulkCopy into GTT → MERGE/INSERT → OPEN REF CURSOR
/// → write IDs/values back into entities → cleanup per BATCH_ID
/// </code>
/// </summary>
internal sealed class OracleBatchUpsertService
{
    private readonly OracleGttSetupSqlGenerator _setup = new();
    private readonly OracleMergeRefCursorSqlGenerator _mergeGenerator = new();
    private readonly OracleInsertRefCursorSqlGenerator _insertGenerator = new();
    private readonly OracleStagingBulkWriter _bulkWriter = new();
    private readonly OracleRefCursorExecutor _executor = new();
    private readonly EntityGeneratedValueBackwriter _backwriter = new();
    private readonly StagingCleanupService _cleanup = new();
    private readonly DuplicateKeyValidator _duplicateKeyValidator = new();

    /// <summary>
    /// Runs the full GTT pipeline for one batch and writes generated values back onto the entities.
    /// </summary>
    public async Task RunAsync<T>(
        bool sync,
        DbContext context,
        OracleDialectBuilder dialect,
        TableMetadata tableInfo,
        IReadOnlyList<T> entities,
        OracleBulkInsertOptions options,
        OnConflictOptions<T>? onConflict,
        bool returnEntities,
        CancellationToken ctk) where T : class
    {
        if (entities.Count == 0)
        {
            return;
        }

        var gttProvider = new OracleGlobalTemporaryTableMetadataProvider(dialect);

        var payloadColumns = tableInfo.GetColumns(includeGenerated: true);
        var insertableColumns = tableInfo.GetColumns(includeGenerated: false);
        var gtt = gttProvider.Create(tableInfo, payloadColumns);

        var plan = dialect.BuildUpsertPlan(
            context, tableInfo, gtt, payloadColumns, insertableColumns, onConflict, returnedColumns: []);

        var isMerge = onConflict != null;
        if (isMerge)
        {
            var matchColumns = plan.MatchColumns
                .Select(qc => payloadColumns.First(c => c.QuotedColumName == qc))
                .ToList();
            _duplicateKeyValidator.Validate(typeof(T), entities, matchColumns, options);
        }

        var connection = (OracleConnection)context.Database.GetDbConnection();
        var connectionInfo = await context.GetConnection(sync, ctk);
        var committed = false;

        var batchId = Guid.NewGuid();

        try
        {
            var transaction = context.Database.CurrentTransaction?.GetDbTransaction();

            // The GTT definition is permanent; create it idempotently so the pipeline also works on a
            // fresh schema without a separate migration step.
            await ExecuteNonQueryAsync(sync, connection, transaction, _setup.BuildCreateTableIfMissingSql(gtt), ctk);

            try
            {
                _bulkWriter.Write(connection, gtt, payloadColumns, entities, batchId, options, ctk);

                var plsql = isMerge ? _mergeGenerator.Build(plan) : _insertGenerator.Build(plan);

                var results = await _executor.ExecuteAsync(
                    sync, connection, transaction, plsql, batchId,
                    returnedColumnCount: plan.ReturnedColumns.Count,
                    hasIdColumn: plan.IdColumn != null,
                    ctk);

                _backwriter.Write(entities, results, plan.IdColumn, plan.ReturnedColumns);
            }
            finally
            {
                // Cleanup must run after the REF CURSOR has been fully read, and is attempted even on
                // failure so no stale rows are left behind for this BATCH_ID.
                await _cleanup.CleanupAsync(sync, connection, transaction, gtt, batchId, CancellationToken.None);
            }

            await connectionInfo.Commit(sync, ctk);
            committed = true;
        }
        finally
        {
            if (!committed)
            {
                await connectionInfo.Rollback(sync);
            }

            await connectionInfo.Close(sync, ctk);
        }
    }

    private static async Task ExecuteNonQueryAsync(
        bool sync,
        OracleConnection connection,
        System.Data.Common.DbTransaction? transaction,
        string sql,
        CancellationToken ctk)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (transaction != null)
        {
            command.Transaction = (OracleTransaction)transaction;
        }

        if (sync)
        {
            // ReSharper disable once MethodHasAsyncOverloadWithCancellation
            command.ExecuteNonQuery();
        }
        else
        {
            await command.ExecuteNonQueryAsync(ctk);
        }
    }
}


