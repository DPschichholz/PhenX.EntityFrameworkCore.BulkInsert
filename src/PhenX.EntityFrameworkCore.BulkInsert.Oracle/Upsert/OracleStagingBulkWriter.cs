using Oracle.ManagedDataAccess.Client;

using PhenX.EntityFrameworkCore.BulkInsert.Metadata;

namespace PhenX.EntityFrameworkCore.BulkInsert.Oracle.Upsert;

/// <summary>
/// Streams a batch of entities into the staging GTT in a single <see cref="OracleBulkCopy"/> pass,
/// writing the technical <see cref="OracleGttColumns.BatchId"/>/<see cref="OracleGttColumns.ClientRowId"/>
/// columns alongside the entity payload.
/// </summary>
internal sealed class OracleStagingBulkWriter
{
    public void Write<T>(
        OracleConnection connection,
        OracleGlobalTemporaryTableMetadata gtt,
        IReadOnlyList<ColumnMetadata> payloadColumns,
        IEnumerable<T> entities,
        Guid batchId,
        OracleBulkInsertOptions options,
        CancellationToken ctk) where T : class
    {
        using var bulkCopy = new OracleBulkCopy(connection, options.CopyOptions)
        {
            DestinationTableName = gtt.QuotedTableName,
            BatchSize = options.BatchSize,
            BulkCopyTimeout = options.GetCopyTimeoutInSeconds(),
            NotifyAfter = options.NotifyProgressAfter ?? options.BatchSize,
        };

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

        bulkCopy.ColumnMappings.Add(OracleGttColumns.BatchId, gtt.BatchIdColumn.ColumnName);
        bulkCopy.ColumnMappings.Add(OracleGttColumns.ClientRowId, gtt.ClientRowIdColumn.ColumnName);
        foreach (var column in payloadColumns)
        {
            bulkCopy.ColumnMappings.Add(column.PropertyName, column.ColumnName);
        }

        using var reader = new OracleStagingDataReader<T>(entities, payloadColumns, batchId, options);

        try
        {
            bulkCopy.WriteToServer(reader);
        }
        catch when (ctk.IsCancellationRequested)
        {
            throw new OperationCanceledException(ctk);
        }
        finally
        {
            bulkCopy.Close();
        }
    }
}

