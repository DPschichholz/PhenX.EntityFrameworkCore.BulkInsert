using DotNet.Testcontainers.Containers;

using Microsoft.EntityFrameworkCore;

using PhenX.EntityFrameworkCore.BulkInsert.PostgreSql;

using Testcontainers.PostgreSql;

namespace PhenX.EntityFrameworkCore.BulkInsert.Benchmark.Providers;

public class UpsertComparatorPostgreSql : UpsertComparator
{
    protected override void ConfigureDbContext()
    {
        var connectionString = GetConnectionString() + ";Include Error Detail=true";

        DbContext = new TestDbContext(p => p
            .UseNpgsql(connectionString)
            .UseBulkInsertPostgreSql()
        );
    }

    protected override IDatabaseContainer? GetDbContainer()
    {
        return new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("testdb")
            .WithUsername("testuser")
            .WithPassword("testpassword")
            .Build();
    }
}

