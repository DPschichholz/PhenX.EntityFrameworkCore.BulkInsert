using FluentAssertions;

using PhenX.EntityFrameworkCore.BulkInsert.Options;
using PhenX.EntityFrameworkCore.BulkInsert.Tests.DbContainer;
using PhenX.EntityFrameworkCore.BulkInsert.Tests.DbContext;

using Xunit;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Tests.Merge;

/// <summary>
/// Integration tests for an entity with an application-assigned Guid primary key, where conflict
/// matching is performed on a separate external (business) key column. Both insert and upsert return
/// the affected entities.
/// </summary>
public abstract class ExternalKeyMergeTestsBase<TDbContext>(IDbContextFactory dbContextFactory) : IAsyncLifetime
    where TDbContext : TestDbContext, new()
{
    private readonly Guid _run = Guid.NewGuid();
    private TDbContext _context = null!;

    public async Task InitializeAsync()
    {
        _context = await dbContextFactory.CreateContextAsync<TDbContext>("basic");
    }

    public Task DisposeAsync()
    {
        _context.Dispose();
        return Task.CompletedTask;
    }

    [SkippableTheory]
    [InlineData(InsertStrategy.InsertReturn)]
    [InlineData(InsertStrategy.InsertReturnAsync)]
    public async Task InsertEntities_WithGuidPk_ReturnsEntities(InsertStrategy strategy)
    {
        // Arrange
        var entities = new List<TestEntityWithExternalKey>
        {
            new() { TestRun = _run, Id = TestHelpers.NewId(), ExternalId = $"{_run}_EXT1", Name = "Name1" },
            new() { TestRun = _run, Id = TestHelpers.NewId(), ExternalId = $"{_run}_EXT2", Name = "Name2" },
        };

        // Act
        var insertedEntities = await _context.InsertWithStrategyAsync(strategy, entities);

        // Assert - all fields including the application-assigned Guid Id and external key are preserved
        insertedEntities.Should().BeEquivalentTo(entities, o => o.RespectingRuntimeTypes());
    }

    [SkippableTheory]
    [InlineData(InsertStrategy.InsertReturn)]
    [InlineData(InsertStrategy.InsertReturnAsync)]
    public async Task UpsertEntities_MatchOnExternalKey_ReturnsUpdatedEntities(InsertStrategy strategy)
    {
        // Arrange - seed an existing row
        var existing = new TestEntityWithExternalKey
        {
            TestRun = _run,
            Id = TestHelpers.NewId(),
            ExternalId = $"{_run}_EXT1",
            Name = "Original",
        };
        _context.TestEntitiesWithExternalKey.Add(existing);
        _context.SaveChanges();
        _context.ChangeTracker.Clear();

        var entities = new List<TestEntityWithExternalKey>
        {
            // Same external key as the existing row -> should update by ExternalId, not by Guid Id.
            new() { TestRun = _run, Id = TestHelpers.NewId(), ExternalId = $"{_run}_EXT1", Name = "Updated" },
            // New external key -> should insert.
            new() { TestRun = _run, Id = TestHelpers.NewId(), ExternalId = $"{_run}_EXT2", Name = "Inserted" },
        };

        // Act
        var insertedEntities = await _context.InsertWithStrategyAsync(strategy, entities, _ => { },
            new OnConflictOptions<TestEntityWithExternalKey>
            {
                Match = e => new { e.ExternalId },
                Update = (inserted, excluded) => new TestEntityWithExternalKey
                {
                    Name = excluded.Name,
                },
            });

        // Assert
        insertedEntities.Should().HaveCount(2);
        insertedEntities.Should().Contain(e => e.ExternalId == $"{_run}_EXT1" && e.Name == "Updated");
        insertedEntities.Should().Contain(e => e.ExternalId == $"{_run}_EXT2" && e.Name == "Inserted");

        // The matched row keeps its original primary key (matching is on ExternalId, not Id).
        _context.ChangeTracker.Clear();
        var persisted = _context.TestEntitiesWithExternalKey
            .Single(e => e.ExternalId == $"{_run}_EXT1");
        persisted.Id.Should().Be(existing.Id);
        persisted.Name.Should().Be("Updated");
    }

    [SkippableTheory]
    [InlineData(InsertStrategy.InsertReturn)]
    [InlineData(InsertStrategy.InsertReturnAsync)]
    public async Task UpsertEntities_UpdateExpressionSetsPrimaryKey_DoesNotOverwritePk(InsertStrategy strategy)
    {
        // Arrange - seed an existing row
        var existing = new TestEntityWithExternalKey
        {
            TestRun = _run,
            Id = TestHelpers.NewId(),
            ExternalId = $"{_run}_EXT1",
            Name = "Original",
        };
        _context.TestEntitiesWithExternalKey.Add(existing);
        _context.SaveChanges();
        _context.ChangeTracker.Clear();

        var entities = new List<TestEntityWithExternalKey>
        {
            // Same external key as the existing row -> should update by ExternalId.
            new() { TestRun = _run, Id = TestHelpers.NewId(), ExternalId = $"{_run}_EXT1", Name = "Updated" },
            // New external key -> should insert.
            new() { TestRun = _run, Id = TestHelpers.NewId(), ExternalId = $"{_run}_EXT2", Name = "Inserted" },
        };

        // Act - the Update expression explicitly assigns the primary key. It must be ignored for the
        // matched row (the PK must never be overwritten), and on Oracle a RAW(16) PK assignment would
        // otherwise raise ORA-01465.
        var insertedEntities = await _context.InsertWithStrategyAsync(strategy, entities, _ => { },
            new OnConflictOptions<TestEntityWithExternalKey>
            {
                Match = e => new { e.ExternalId },
                Update = (inserted, excluded) => new TestEntityWithExternalKey
                {
                    Id = TestHelpers.NewId(),
                    Name = excluded.Name,
                },
            });

        // Assert
        insertedEntities.Should().HaveCount(2);
        insertedEntities.Should().Contain(e => e.ExternalId == $"{_run}_EXT1" && e.Name == "Updated");
        insertedEntities.Should().Contain(e => e.ExternalId == $"{_run}_EXT2" && e.Name == "Inserted");

        // The matched row keeps its original primary key even though the Update expression set a new Id.
        _context.ChangeTracker.Clear();
        var persisted = _context.TestEntitiesWithExternalKey
            .Single(e => e.ExternalId == $"{_run}_EXT1");
        persisted.Id.Should().Be(existing.Id);
        persisted.Name.Should().Be("Updated");
    }
}


