using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

using PhenX.EntityFrameworkCore.BulkInsert.Abstractions;
using PhenX.EntityFrameworkCore.BulkInsert.Metadata;
using PhenX.EntityFrameworkCore.BulkInsert.Options;
using PhenX.EntityFrameworkCore.BulkInsert.PostgreSql;
using PhenX.EntityFrameworkCore.BulkInsert.Oracle;
using PhenX.EntityFrameworkCore.BulkInsert.Tests.DbContext;

using Xunit;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Tests.Merge;

/// <summary>
/// Container-free unit tests asserting the SQL generated for an entity whose primary key is a Guid,
/// while conflict matching is performed on a separate external (business) key column.
/// These run without Docker and guard the SQL shape before exercising the live databases.
/// </summary>
public class ExternalKeyMergeSqlTests
{
    private static OnConflictOptions<TestEntityWithExternalKey> CreateMatchOnExternalKey() => new()
    {
        Match = e => new { e.ExternalId },
        Update = (inserted, excluded) => new TestEntityWithExternalKey { Name = excluded.Name },
    };

    private static (string insert, string merge) BuildSql(Microsoft.EntityFrameworkCore.DbContext context)
    {
        var provider = context.GetService<IBulkInsertProvider>();
        var tableInfo = new MetadataProvider().GetTableInfo<TestEntityWithExternalKey>(context);
        var insertedColumns = tableInfo.GetColumns(includeGenerated: false);

        var insert = provider.SqlDialect.BuildMoveDataSql<TestEntityWithExternalKey>(
            context,
            tableInfo,
            "source_temp",
            insertedColumns,
            [],
            new BulkInsertOptions());

        var merge = provider.SqlDialect.BuildMoveDataSql<TestEntityWithExternalKey>(
            context,
            tableInfo,
            "source_temp",
            insertedColumns,
            [],
            new BulkInsertOptions(),
            CreateMatchOnExternalKey());

        return (insert, merge);
    }

    [Fact]
    public void PostgreSql_Insert_IncludesGuidPrimaryKeyColumn()
    {
        using var context = CreatePostgreSqlContext();

        var (insert, _) = BuildSql(context);

        insert.Should().Contain("INSERT INTO");
        insert.Should().Contain("\"id\"");
    }

    [Fact]
    public void PostgreSql_Merge_MatchesOnExternalKey_NotPrimaryKey()
    {
        using var context = CreatePostgreSqlContext();

        var (_, merge) = BuildSql(context);

        merge.Should().Contain("ON CONFLICT");
        merge.Should().Contain("\"external_id\"");
        // The Guid primary key must not be used as the conflict target.
        merge.Should().NotContain("ON CONFLICT (\"id\")");
        merge.Should().Contain("DO UPDATE");
    }

    [Fact]
    public void Oracle_Insert_IncludesGuidPrimaryKeyColumn()
    {
        using var context = CreateOracleContext();

        var (insert, _) = BuildSql(context);

        insert.Should().Contain("INSERT INTO");
        insert.Should().Contain("\"id\"");
    }

    [Fact]
    public void Oracle_Merge_MatchesOnExternalKey()
    {
        using var context = CreateOracleContext();

        var (_, merge) = BuildSql(context);

        merge.Should().Contain("MERGE INTO");
        merge.Should().Contain("\"external_id\"");
        // Oracle plain (non PL/SQL) statements must not end with a terminator (guards against ORA-03048).
        merge.TrimEnd().Should().NotEndWith(";");
    }

    private static TestDbContextPostgreSql CreatePostgreSqlContext() => new()
    {
        ConfigureOptions = options => options
            .UseNpgsql("Host=dummy;Database=d;Username=u;Password=p")
            .UseBulkInsertPostgreSql()
    };

    private static TestDbContextOracle CreateOracleContext() => new()
    {
        ConfigureOptions = options => options
            .UseOracle("Data Source=dummy;User Id=u;Password=p;")
            .UseBulkInsertOracle()
    };
}

