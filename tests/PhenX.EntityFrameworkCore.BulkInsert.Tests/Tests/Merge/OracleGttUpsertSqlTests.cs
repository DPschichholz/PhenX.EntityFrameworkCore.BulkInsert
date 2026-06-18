using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

using PhenX.EntityFrameworkCore.BulkInsert.Abstractions;
using PhenX.EntityFrameworkCore.BulkInsert.Metadata;
using PhenX.EntityFrameworkCore.BulkInsert.Oracle;
using PhenX.EntityFrameworkCore.BulkInsert.Oracle.Upsert;
using PhenX.EntityFrameworkCore.BulkInsert.Tests.DbContext;

using Xunit;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Tests.Merge;

/// <summary>
/// Container-free unit tests for the Oracle 21c GTT REF CURSOR SQL generators.
/// </summary>
[Trait("Category", "Oracle")]
public class OracleGttUpsertSqlTests
{
    private static TestDbContextOracle CreateContext() => new()
    {
        ConfigureOptions = options => options
            .UseOracle("Data Source=dummy;User Id=u;Password=p;")
            .UseBulkInsertOracle(),
    };

    private static OracleGlobalTemporaryTableMetadata BuildGtt(TestDbContextOracle context, TableMetadata tableInfo)
    {
        var dialect = (OracleDialectBuilder)context.GetService<IBulkInsertProvider>().SqlDialect;
        var provider = new OracleGlobalTemporaryTableMetadataProvider(dialect);
        return provider.Create(tableInfo, tableInfo.GetColumns(includeGenerated: true));
    }

    private static ColumnMetadata Column(TableMetadata tableInfo, string property) =>
        tableInfo.GetColumns(includeGenerated: true).First(c => c.PropertyName == property);

    [Fact]
    public void GttSetup_CreatesGlobalTemporaryTable_WithTechnicalColumns()
    {
        using var context = CreateContext();
        var tableInfo = new MetadataProvider().GetTableInfo<TestEntity>(context);
        var gtt = BuildGtt(context, tableInfo);

        var sql = new OracleGttSetupSqlGenerator().BuildCreateTableSql(gtt);

        sql.Should().Contain("CREATE GLOBAL TEMPORARY TABLE");
        sql.Should().Contain("ON COMMIT PRESERVE ROWS");
        sql.Should().Contain("\"BATCH_ID\" RAW(16) NOT NULL");
        sql.Should().Contain("\"CLIENT_ROW_ID\" NUMBER(19) NOT NULL");
        // Payload columns are staged as nullable so partial payloads can be written.
        sql.Should().Contain("\"name\"");
    }

    [Fact]
    public void Merge_BuildsMergeWithBatchFilterAndOpenRefCursor()
    {
        using var context = CreateContext();
        var tableInfo = new MetadataProvider().GetTableInfo<TestEntity>(context);
        var gtt = BuildGtt(context, tableInfo);

        var plan = new OracleUpsertPlan
        {
            Gtt = gtt,
            TargetQuotedTableName = tableInfo.QuotedTableName,
            MatchColumns = [tableInfo.GetQuotedColumnName(nameof(TestEntity.Name))],
            InsertColumns = [Column(tableInfo, nameof(TestEntity.Name)), Column(tableInfo, nameof(TestEntity.Price))],
            UpdateClauses = [$"{tableInfo.GetQuotedColumnName(nameof(TestEntity.Price))} = src.{tableInfo.GetQuotedColumnName(nameof(TestEntity.Price))}"],
            IdColumn = tableInfo.PrimaryKey[0],
            IdStrategy = OracleIdStrategy.Sequence,
            SequenceName = "TEST_SEQ",
        };

        var sql = new OracleMergeRefCursorSqlGenerator().Build(plan);

        sql.Should().Contain("MERGE INTO");
        sql.Should().Contain("\"BATCH_ID\" = :batch_id");
        sql.Should().Contain("WHEN MATCHED THEN UPDATE SET");
        sql.Should().Contain("WHEN NOT MATCHED THEN INSERT");
        sql.Should().Contain("\"TEST_SEQ\".NEXTVAL");
        sql.Should().Contain("OPEN :rc FOR");
        sql.Should().Contain("\"CLIENT_ROW_ID\" AS \"CLIENT_ROW_ID\"");
        // No Oracle 23ai MERGE RETURNING.
        System.Text.RegularExpressions.Regex
            .IsMatch(sql, @"MERGE[\s\S]*RETURNING")
            .Should().BeFalse("the pipeline must not rely on MERGE RETURNING");
    }

    [Fact]
    public void Merge_Identity_DoesNotInsertIdColumn()
    {
        using var context = CreateContext();
        var tableInfo = new MetadataProvider().GetTableInfo<TestEntity>(context);
        var gtt = BuildGtt(context, tableInfo);

        var idColumn = tableInfo.PrimaryKey[0];
        var plan = new OracleUpsertPlan
        {
            Gtt = gtt,
            TargetQuotedTableName = tableInfo.QuotedTableName,
            MatchColumns = [tableInfo.GetQuotedColumnName(nameof(TestEntity.Name))],
            InsertColumns = [Column(tableInfo, nameof(TestEntity.Name)), Column(tableInfo, nameof(TestEntity.Price))],
            IdColumn = idColumn,
            IdStrategy = OracleIdStrategy.Identity,
        };

        var sql = new OracleMergeRefCursorSqlGenerator().Build(plan);

        // Identity: the ID must not be part of the INSERT branch, but is still read back via the cursor.
        System.Text.RegularExpressions.Regex
            .IsMatch(sql, $@"INSERT\s*\([^)]*{System.Text.RegularExpressions.Regex.Escape(idColumn.QuotedColumName)}")
            .Should().BeFalse();
        sql.Should().Contain($"tgt.{idColumn.QuotedColumName}");
    }

    [Fact]
    public void Merge_ClientGeneratedGuidId_InsertsIdFromSource()
    {
        using var context = CreateContext();
        var tableInfo = new MetadataProvider().GetTableInfo<TestEntityWithGeneratedGuidId>(context);
        var gtt = BuildGtt(context, tableInfo);

        var idColumn = tableInfo.PrimaryKey[0];
        var plan = new OracleUpsertPlan
        {
            Gtt = gtt,
            TargetQuotedTableName = tableInfo.QuotedTableName,
            MatchColumns = [tableInfo.GetQuotedColumnName(nameof(TestEntityWithGeneratedGuidId.Name))],
            InsertColumns = [idColumn, Column(tableInfo, nameof(TestEntityWithGeneratedGuidId.Name))],
            IdColumn = idColumn,
            IdStrategy = OracleIdStrategy.ClientGenerated,
        };

        var sql = new OracleMergeRefCursorSqlGenerator().Build(plan);

        sql.Should().Contain($"src.{idColumn.QuotedColumName}");
        sql.Should().NotContain(".NEXTVAL");
    }

    [Fact]
    public void Merge_CompositeMatch_UsesAllMatchColumns()
    {
        using var context = CreateContext();
        var tableInfo = new MetadataProvider().GetTableInfo<TestEntity>(context);
        var gtt = BuildGtt(context, tableInfo);

        var name = tableInfo.GetQuotedColumnName(nameof(TestEntity.Name));
        var identifier = tableInfo.GetQuotedColumnName(nameof(TestEntity.Identifier));

        var plan = new OracleUpsertPlan
        {
            Gtt = gtt,
            TargetQuotedTableName = tableInfo.QuotedTableName,
            MatchColumns = [name, identifier],
            InsertColumns = [Column(tableInfo, nameof(TestEntity.Name)), Column(tableInfo, nameof(TestEntity.Identifier))],
            IdColumn = tableInfo.PrimaryKey[0],
            IdStrategy = OracleIdStrategy.Identity,
        };

        var sql = new OracleMergeRefCursorSqlGenerator().Build(plan);

        sql.Should().Contain($"tgt.{name} = src.{name}");
        sql.Should().Contain($"tgt.{identifier} = src.{identifier}");
    }

    [Fact]
    public void Insert_BuildsInsertSelectAndRefCursorFromStaging()
    {
        using var context = CreateContext();
        var tableInfo = new MetadataProvider().GetTableInfo<TestEntity>(context);
        var gtt = BuildGtt(context, tableInfo);

        var plan = new OracleUpsertPlan
        {
            Gtt = gtt,
            TargetQuotedTableName = tableInfo.QuotedTableName,
            MatchColumns = [tableInfo.GetQuotedColumnName(nameof(TestEntity.Name))],
            InsertColumns = [Column(tableInfo, nameof(TestEntity.Name)), Column(tableInfo, nameof(TestEntity.Price))],
            IdColumn = tableInfo.PrimaryKey[0],
            IdStrategy = OracleIdStrategy.Sequence,
            SequenceName = "TEST_SEQ",
        };

        var sql = new OracleInsertRefCursorSqlGenerator().Build(plan);

        sql.Should().Contain("\"TEST_SEQ\".NEXTVAL");
        sql.Should().Contain("INSERT INTO");
        sql.Should().Contain("\"BATCH_ID\" = :batch_id");
        sql.Should().Contain("OPEN :rc FOR");
        sql.Should().Contain("\"CLIENT_ROW_ID\" AS \"CLIENT_ROW_ID\"");
    }

    [Fact]
    public void Insert_Identity_IsNotSupported()
    {
        using var context = CreateContext();
        var tableInfo = new MetadataProvider().GetTableInfo<TestEntity>(context);
        var gtt = BuildGtt(context, tableInfo);

        var plan = new OracleUpsertPlan
        {
            Gtt = gtt,
            TargetQuotedTableName = tableInfo.QuotedTableName,
            MatchColumns = [tableInfo.GetQuotedColumnName(nameof(TestEntity.Name))],
            InsertColumns = [Column(tableInfo, nameof(TestEntity.Name))],
            IdColumn = tableInfo.PrimaryKey[0],
            IdStrategy = OracleIdStrategy.Identity,
        };

        var act = () => new OracleInsertRefCursorSqlGenerator().Build(plan);

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Backwriter_WritesRawGuidIdBack_FromByteArray()
    {
        using var context = CreateContext();
        var tableInfo = new MetadataProvider().GetTableInfo<TestEntityWithGeneratedGuidId>(context);
        var idColumn = tableInfo.PrimaryKey[0];

        var expected = Guid.NewGuid();
        var entities = new List<TestEntityWithGeneratedGuidId> { new() { Name = "x" } };

        // ODP.NET returns a RAW(16) Guid as a byte[] (Guid.ToByteArray() order); the backwriter must
        // reconstruct the Guid instead of failing with "Object must implement IConvertible".
        var results = new List<UpsertResultRow>
        {
            new() { ClientRowId = 0, Id = expected.ToByteArray() },
        };

        new EntityGeneratedValueBackwriter().Write(entities, results, idColumn, returnedColumns: []);

        entities[0].Id.Should().Be(expected);
    }
}



