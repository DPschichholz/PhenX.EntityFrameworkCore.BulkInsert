using DotNet.Testcontainers.Containers;

using Microsoft.EntityFrameworkCore;

using PhenX.EntityFrameworkCore.BulkInsert.Sqlite;

namespace PhenX.EntityFrameworkCore.BulkInsert.Benchmark.Providers;

public class UpsertComparatorSqlite : UpsertComparator
{
    protected override void ConfigureDbContext()
    {
        var connectionString = GetConnectionString();

        DbContext = new TestDbContext(p => p
            .UseSqlite(connectionString)
            .UseBulkInsertSqlite()
        );
    }

    protected override string GetConnectionString()
    {
        return $"Data Source={Guid.NewGuid()}.db";
    }

    protected override IDatabaseContainer? GetDbContainer() => null;
}

