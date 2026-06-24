using Microsoft.EntityFrameworkCore;

using PhenX.EntityFrameworkCore.BulkInsert.Enums;
using PhenX.EntityFrameworkCore.BulkInsert.Extensions;
using PhenX.EntityFrameworkCore.BulkInsert.Options;
using PhenX.EntityFrameworkCore.BulkInsert.Tests.DbContext;

using Xunit;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests;

public enum InsertStrategy
{
    Insert,
    InsertReturn,
    InsertAsync,
    InsertReturnAsync
}

public static class TestHelpers
{
    /// <summary>
    /// Creates a new application-side primary key value. Uses time-ordered UUIDv7 on .NET 9+
    /// (better index locality) and falls back to <see cref="Guid.NewGuid"/> on .NET 8.
    /// </summary>
    public static Guid NewId()
    {
#if NET9_0_OR_GREATER
        return Guid.CreateVersion7();
#else
        return Guid.NewGuid();
#endif
    }

    public static async Task<List<T>> InsertWithStrategyAsync<T>(
        this TestDbContextBase dbContext,
        InsertStrategy strategy,
        List<T> entities,
        Action<BulkInsertOptions>? configure = null,
        OnConflictOptions<T>? onConflict = null)
        where T : TestEntityBase
    {
        ProviderType[] returningNotSupported = [
            ProviderType.MySql,
        ];

        Skip.If(strategy is InsertStrategy.InsertReturn or InsertStrategy.InsertReturnAsync && dbContext.IsProvider(returningNotSupported));

        var runId = Guid.NewGuid();
        if (entities.Any(x => x.TestRun == default))
        {
            foreach (var entity in entities)
            {
                if (entity.TestRun == default)
                {
                    entity.TestRun = runId;
                }
            }
        }
        else if (entities.Count > 0)
        {
            runId = entities[0].TestRun;
        }

        var actualConfigure = configure ?? (_ => { });
        try
        {
            switch (strategy)
            {
                case InsertStrategy.InsertReturn:
                    return dbContext.ExecuteBulkInsertReturnEntities(entities, actualConfigure, onConflict);
                case InsertStrategy.InsertReturnAsync:
                    return await dbContext.ExecuteBulkInsertReturnEntitiesAsync(entities, actualConfigure, onConflict);
                case InsertStrategy.Insert:
                    dbContext.ExecuteBulkInsert(entities, actualConfigure, onConflict);
                    return dbContext.Set<T>().Where(x => x.TestRun == runId).ToList();
                case InsertStrategy.InsertAsync:
                    await dbContext.ExecuteBulkInsertAsync(entities, actualConfigure, onConflict);
                    return await dbContext.Set<T>().Where(x => x.TestRun == runId).ToListAsync();
                default:
                    throw new NotImplementedException();
            }
        }
        finally
        {
            dbContext.ChangeTracker.Clear();
        }
    }
}
