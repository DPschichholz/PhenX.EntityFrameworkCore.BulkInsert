using System.Data;

using Oracle.ManagedDataAccess.Client;

namespace PhenX.EntityFrameworkCore.BulkInsert.Oracle.Upsert;

/// <summary>
/// Removes the staged rows of a single run from the GTT. Because the GTT uses
/// <c>ON COMMIT PRESERVE ROWS</c>, cleanup is explicit and always scoped to the run's
/// <see cref="OracleGttColumns.BatchId"/> — never a global <c>DELETE</c> or <c>TRUNCATE</c>, so
/// other logical batches in the same session are untouched.
/// </summary>
internal sealed class StagingCleanupService
{
    public async Task CleanupAsync(
        bool sync,
        OracleConnection connection,
        IDbTransaction? transaction,
        OracleGlobalTemporaryTableMetadata gtt,
        Guid batchId,
        CancellationToken ctk)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"DELETE FROM {gtt.QuotedTableName} WHERE \"{OracleGttColumns.BatchId}\" = :{OracleUpsertParameters.BatchId}";
        command.BindByName = true;
        if (transaction is OracleTransaction oracleTransaction)
        {
            command.Transaction = oracleTransaction;
        }

        command.Parameters.Add(new OracleParameter(OracleUpsertParameters.BatchId, OracleDbType.Raw)
        {
            Value = batchId.ToByteArray(),
            Direction = ParameterDirection.Input,
        });

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

