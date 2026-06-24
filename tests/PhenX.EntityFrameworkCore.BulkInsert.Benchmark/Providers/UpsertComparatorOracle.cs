using DotNet.Testcontainers.Containers;

using Microsoft.EntityFrameworkCore;

using PhenX.EntityFrameworkCore.BulkInsert.Oracle;

using Testcontainers.Oracle;

namespace PhenX.EntityFrameworkCore.BulkInsert.Benchmark.Providers;

public class UpsertComparatorOracle : UpsertComparator
{
    protected override void ConfigureDbContext()
    {
        var connectionString = GetConnectionString();

        DbContext = new TestDbContext(p => p
            .UseOracle(connectionString)
            .UseBulkInsertOracle()
        );
    }

    protected override IDatabaseContainer? GetDbContainer()
    {
        return new OracleBuilder("gvenzl/oracle-free:23.6-slim-faststart")
            .Build();
    }
}

