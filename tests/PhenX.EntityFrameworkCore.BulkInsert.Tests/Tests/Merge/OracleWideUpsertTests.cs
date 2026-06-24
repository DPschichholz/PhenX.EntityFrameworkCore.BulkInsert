using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using PhenX.EntityFrameworkCore.BulkInsert.Extensions;
using PhenX.EntityFrameworkCore.BulkInsert.Options;
using PhenX.EntityFrameworkCore.BulkInsert.Tests.DbContainer;
using PhenX.EntityFrameworkCore.BulkInsert.Tests.DbContext;

using Xunit;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Tests.Merge;

/// <summary>
/// Integration tests for the Oracle GTT REF CURSOR upsert pipeline using a wide (~23 column) payload.
/// Exercises insert + update in one batch, generated-ID write-back via CLIENT_ROW_ID, and BATCH_ID
/// scoped cleanup. Skipped automatically when the Oracle container is unavailable.
/// </summary>
[Trait("Category", "Oracle")]
[Collection(TestDbContainerOracleCollection.Name)]
public class OracleWideUpsertTests(TestDbContainerOracle dbContainer) : IAsyncLifetime
{
    private readonly Guid _run = Guid.NewGuid();
    private TestDbContextOracle _context = null!;

    public async Task InitializeAsync()
    {
        _context = await dbContainer.CreateContextAsync<TestDbContextOracle>("basic");
    }

    public Task DisposeAsync()
    {
        _context.Dispose();
        return Task.CompletedTask;
    }

    private OracleWideUpsertEntity NewEntity(string code, int seed) => new()
    {
        Code = $"{_run}_{code}",
        String1 = $"s1_{seed}",
        String2 = $"s2_{seed}",
        String3 = $"s3_{seed}",
        String4 = $"s4_{seed}",
        String5 = $"s5_{seed}",
        Int1 = seed,
        Int2 = seed * 2,
        Int3 = seed % 2 == 0 ? seed : null,
        Long1 = seed * 1000L,
        Long2 = seed,
        Decimal1 = seed + 0.5m,
        Decimal2 = seed % 3 == 0 ? null : seed - 0.25m,
        Double1 = seed * 1.5,
        Bool1 = seed % 2 == 0,
        Bool2 = seed % 3 == 0 ? null : seed % 2 == 1,
        Date1 = new DateTime(2024, 1, 1).AddDays(seed),
        Date2 = seed % 2 == 0 ? new DateTime(2024, 6, 1).AddDays(seed) : null,
        Guid1 = Guid.NewGuid(),
        Guid2 = seed % 2 == 0 ? Guid.NewGuid() : null,
        Short1 = (short)seed,
        Byte1 = (byte)(seed % 256),
    };

    [SkippableFact]
    public async Task Upsert_WidePayload_InsertsAndUpdates_WritesBackIds()
    {
        // Arrange - seed one existing row so the batch contains both an update and an insert.
        var existing = NewEntity("A", 1);
        _context.Set<OracleWideUpsertEntity>().Add(existing);
        await _context.SaveChangesAsync();
        var existingId = existing.Id;
        _context.ChangeTracker.Clear();

        var batch = new List<OracleWideUpsertEntity>
        {
            NewEntity("A", 11), // same Code -> update existing
            NewEntity("B", 2),  // new Code -> insert
        };

        // Act
        var returned = await _context.ExecuteBulkInsertReturnEntitiesAsync(
            batch,
            new OnConflictOptions<OracleWideUpsertEntity>
            {
                Match = e => new { e.Code },
                Update = (inserted, excluded) => new OracleWideUpsertEntity
                {
                    String1 = excluded.String1,
                    Int1 = excluded.Int1,
                    Decimal1 = excluded.Decimal1,
                },
            });

        // Assert - generated IDs are written back onto the originating instances.
        returned.Should().HaveCount(2);
        batch.Should().OnlyContain(e => e.Id > 0);

        var updated = batch.Single(e => e.Code == $"{_run}_A");
        updated.Id.Should().Be(existingId, "an existing row keeps its primary key");

        var inserted = batch.Single(e => e.Code == $"{_run}_B");
        inserted.Id.Should().NotBe(existingId);

        // Assert persisted state.
        _context.ChangeTracker.Clear();
        var persistedA = _context.Set<OracleWideUpsertEntity>().Single(e => e.Code == $"{_run}_A");
        persistedA.Id.Should().Be(existingId);
        persistedA.String1.Should().Be("s1_11");
        persistedA.Int1.Should().Be(11);

        var persistedB = _context.Set<OracleWideUpsertEntity>().Single(e => e.Code == $"{_run}_B");
        persistedB.Id.Should().Be(inserted.Id);
        persistedB.String5.Should().Be("s5_2");
    }

    [SkippableFact]
    public async Task Upsert_WithConstantBool_EmitsNumericLiteral_AndUpdates()
    {
        // Arrange - seed an existing row whose boolean column is FALSE so the constant update is observable.
        var existing = NewEntity("C", 1);
        existing.Bool1 = false;
        _context.Set<OracleWideUpsertEntity>().Add(existing);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var batch = new List<OracleWideUpsertEntity>
        {
            NewEntity("C", 21), // same Code -> update existing
        };

        // Act - the Update expression assigns a constant 'true', which must be emitted as the Oracle
        // numeric literal 1 (not the SQL standard TRUE, which is invalid on Oracle 21c / NUMBER(1)).
        await _context.ExecuteBulkInsertAsync(
            batch,
            new OnConflictOptions<OracleWideUpsertEntity>
            {
                Match = e => new { e.Code },
                Update = (inserted, excluded) => new OracleWideUpsertEntity
                {
                    Bool1 = true,
                },
            });

        // Assert - the constant boolean was applied.
        _context.ChangeTracker.Clear();
        var persisted = _context.Set<OracleWideUpsertEntity>().Single(e => e.Code == $"{_run}_C");
        persisted.Bool1.Should().BeTrue();
    }
}

