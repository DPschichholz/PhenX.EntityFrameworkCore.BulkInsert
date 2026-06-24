using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using PhenX.EntityFrameworkCore.BulkInsert.Extensions;
using PhenX.EntityFrameworkCore.BulkInsert.Options;
using PhenX.EntityFrameworkCore.BulkInsert.Tests.DbContainer;
using PhenX.EntityFrameworkCore.BulkInsert.Tests.DbContext;

using Xunit;
using Xunit.Abstractions;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Tests.Merge;

/// <summary>
/// Debuggable end-to-end test for the Oracle GTT REF CURSOR pipeline with a client-generated Guid
/// primary key (mirrors the standalone Product scenario). Requires Docker (Testcontainers).
/// Breakpoints: OracleBatchUpsertService.RunAsync, OracleMergeRefCursorSqlGenerator.Build,
/// OracleInsertRefCursorSqlGenerator.Build, EntityGeneratedValueBackwriter.Write.
/// </summary>
[Trait("Category", "Oracle")]
[Collection(TestDbContainerOracleCollection.Name)]
public class OracleGuidUpsertDebugTests(TestDbContainerOracle dbContainer, ITestOutputHelper output) : IAsyncLifetime
{
    private readonly Guid _run = Guid.NewGuid();
    private TestDbContextOracle _context = null!;

    public async Task InitializeAsync()
    {
        _context = await dbContainer.CreateContextAsync<TestDbContextOracle>("guiddebug");
    }

    public Task DisposeAsync()
    {
        _context.Dispose();
        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task GuidUpsert_AllScenarios_Debuggable()
    {
        // ---- Step 1: bulk insert (client-generated Guid PK, no conflict) ----
        // Breakpoints: OracleInsertRefCursorSqlGenerator.Build, EntityGeneratedValueBackwriter.Write
        var products = new List<TestEntityWithGeneratedGuidId>
        {
            new() { TestRun = _run, Name = $"{_run}_Widget A" },
            new() { TestRun = _run, Name = $"{_run}_Widget B" },
        };

        var insertedEntities = await _context.ExecuteBulkInsertReturnEntitiesAsync(products);

        insertedEntities.Should().HaveCount(2);
        products.Should().OnlyContain(p => p.Id != Guid.Empty);
        var idA = products[0].Id;
        Dump("After insert", products);

        // ---- Step 2: upsert (update A, insert C) matching on Name ----
        // Breakpoints: OracleMergeRefCursorSqlGenerator.Build, OracleBatchUpsertService.RunAsync
        products[0].Name = $"{_run}_Widget A";                              // same match key -> update
        products.Add(new() { TestRun = _run, Name = $"{_run}_Widget C" });  // new -> insert

        var upserted = await _context.ExecuteBulkInsertReturnEntitiesAsync(
            products,
            new OnConflictOptions<TestEntityWithGeneratedGuidId>
            {
                Match = p => new { p.Name },
                Update = (inserted, excluded) => new TestEntityWithGeneratedGuidId
                {
                    Name = excluded.Name,
                },
            });

        upserted.Should().HaveCount(3);
        // The matched row keeps its original Guid Id (matching on Name, not Id).
        products.Single(p => p.Name == $"{_run}_Widget A").Id.Should().Be(idA);
        products.Single(p => p.Name == $"{_run}_Widget C").Id.Should().NotBe(Guid.Empty);
        Dump("After upsert", products);

        // ---- Step 3: partial upsert without returning entities ----
        var partial = new List<TestEntityWithGeneratedGuidId>
        {
            new() { TestRun = _run, Name = $"{_run}_Widget B" },
        };

        await _context.ExecuteBulkInsertAsync(
            partial,
            onConflict: new OnConflictOptions<TestEntityWithGeneratedGuidId>
            {
                Match = p => new { p.Name },
                Update = (inserted, excluded) => new TestEntityWithGeneratedGuidId
                {
                    Name = excluded.Name,
                },
            });

        // ---- Step 4: verify persisted state ----
        _context.ChangeTracker.Clear();
        var persisted = await _context.Set<TestEntityWithGeneratedGuidId>()
            .AsNoTracking()
            .Where(p => p.TestRun == _run)
            .ToListAsync();

        persisted.Should().HaveCount(3);
        persisted.Should().Contain(p => p.Name == $"{_run}_Widget A" && p.Id == idA);
        persisted.Should().Contain(p => p.Name == $"{_run}_Widget C");
        Dump("Persisted", persisted);
    }

    private void Dump(string title, IEnumerable<TestEntityWithGeneratedGuidId> rows)
    {
        output.WriteLine($"--- {title} ---");
        foreach (var p in rows)
        {
            output.WriteLine($"  {p.Id}  {p.Name}");
        }
    }
}
