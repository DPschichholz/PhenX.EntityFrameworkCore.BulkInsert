using System.Data;

using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace PhenX.EntityFrameworkCore.BulkInsert.Oracle.Upsert;

/// <summary>
/// Executes a GTT PL/SQL block, binding <c>:batch_id</c> by name and registering the <c>:rc</c>
/// REF CURSOR output, then reads the result set fully into <see cref="UpsertResultRow"/> instances.
/// </summary>
internal sealed class OracleRefCursorExecutor
{
    public async Task<IReadOnlyList<UpsertResultRow>> ExecuteAsync(
        bool sync,
        OracleConnection connection,
        IDbTransaction? transaction,
        string plsql,
        Guid batchId,
        int returnedColumnCount,
        bool hasIdColumn,
        CancellationToken ctk)
    {
        using var command = connection.CreateCommand();
        command.CommandText = plsql;
        command.BindByName = true;
        if (transaction is OracleTransaction oracleTransaction)
        {
            command.Transaction = oracleTransaction;
        }

        var batchIdParam = new OracleParameter(OracleUpsertParameters.BatchId, OracleDbType.Raw)
        {
            Value = batchId.ToByteArray(),
            Direction = ParameterDirection.Input,
        };

        var cursorParam = new OracleParameter(OracleUpsertParameters.RefCursor, OracleDbType.RefCursor)
        {
            Direction = ParameterDirection.Output,
        };

        command.Parameters.Add(batchIdParam);
        command.Parameters.Add(cursorParam);

        try
        {
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
        catch (OracleException ex)
        {
            throw new InvalidOperationException(
                $"The Oracle bulk upsert PL/SQL block failed (ORA-{ex.Number:00000}): {ex.Message}", ex);
        }

        var results = new List<UpsertResultRow>();

        if (cursorParam.Value is not OracleRefCursor refCursor)
        {
            return results;
        }

        using var reader = refCursor.GetDataReader();
        var idOrdinal = hasIdColumn ? 1 : -1;
        var firstReturnedOrdinal = hasIdColumn ? 2 : 1;

        while (sync ? reader.Read() : await reader.ReadAsync(ctk))
        {
            var clientRowId = Convert.ToInt64(reader.GetValue(0));
            var id = idOrdinal >= 0 ? Normalize(reader.GetValue(idOrdinal)) : null;

            var returned = new object?[returnedColumnCount];
            for (var i = 0; i < returnedColumnCount; i++)
            {
                returned[i] = Normalize(reader.GetValue(firstReturnedOrdinal + i));
            }

            results.Add(new UpsertResultRow
            {
                ClientRowId = clientRowId,
                Id = id,
                ReturnedValues = returned,
            });
        }

        return results;
    }

    private static object? Normalize(object value) => value is DBNull ? null : value;
}

