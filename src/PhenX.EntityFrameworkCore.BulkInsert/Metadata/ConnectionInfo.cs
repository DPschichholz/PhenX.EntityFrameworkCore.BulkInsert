using System.Data.Common;

using Microsoft.EntityFrameworkCore.Storage;

namespace PhenX.EntityFrameworkCore.BulkInsert.Metadata;

internal readonly record struct ConnectionInfo(DbConnection Connection, bool WasClosed, IDbContextTransaction Transaction, bool WasBegan)
{
    public async Task Commit(bool sync, CancellationToken ctk)
    {
        if (!WasBegan)
        {
            if (sync)
            {
                // ReSharper disable once MethodHasAsyncOverloadWithCancellation
                Transaction.Commit();
            }
            else
            {
                await Transaction.CommitAsync(ctk);
            }
        }
    }

    /// <summary>
    /// Rolls back the transaction if it was started by the library (not owned by the caller).
    /// Used on cancellation or failure to release server-side locks promptly instead of relying on
    /// the implicit rollback performed by <see cref="Close"/>'s dispose.
    /// </summary>
    public async Task Rollback(bool sync)
    {
        if (WasBegan)
        {
            // The caller owns the transaction; do not roll back on their behalf.
            return;
        }

        try
        {
            if (sync)
            {
                // ReSharper disable once MethodHasAsyncOverloadWithCancellation
                Transaction.Rollback();
            }
            else
            {
                await Transaction.RollbackAsync(CancellationToken.None);
            }
        }
        catch
        {
            // Best-effort: the transaction may already be rolled back or unusable; Close will dispose it.
        }
    }

    public async Task Close(bool sync, CancellationToken ctk)
    {
        if (!WasBegan)
        {
            if (sync)
            {
                // ReSharper disable once MethodHasAsyncOverloadWithCancellation
                Transaction.Dispose();
            }
            else
            {
                await Transaction.DisposeAsync();
            }
        }

        if (WasClosed)
        {
            if (sync)
            {
                // ReSharper disable once MethodHasAsyncOverload
                Connection.Close();
            }
            else
            {
                await Connection.CloseAsync();
            }
        }
    }
}
