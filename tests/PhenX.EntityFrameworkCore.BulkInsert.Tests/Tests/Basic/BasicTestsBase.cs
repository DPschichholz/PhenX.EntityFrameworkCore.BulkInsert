using FluentAssertions;
using FluentAssertions.Extensions;

using PhenX.EntityFrameworkCore.BulkInsert.Enums;
using PhenX.EntityFrameworkCore.BulkInsert.Extensions;
using PhenX.EntityFrameworkCore.BulkInsert.SqlServer;
using PhenX.EntityFrameworkCore.BulkInsert.Tests.DbContainer;
using PhenX.EntityFrameworkCore.BulkInsert.Tests.DbContext;

using Xunit;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Tests.Basic;

public abstract class BasicTestsBase<TDbContext>(IDbContextFactory dbContextFactory) : IAsyncLifetime
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
    [CombinatorialData]
    public async Task InsertsEntities(InsertStrategy strategy)
    {
        // Arrange
        var entities = new List<TestEntity>
        {
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity1" },
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity2" }
        };

        // Act
        var insertedEntities = await _context.InsertWithStrategyAsync(strategy, entities);

        // Assert
        insertedEntities.Should().BeEquivalentTo(entities,
            o => o.RespectingRuntimeTypes().Excluding(e => e.Id));
    }

    [SkippableTheory]
    [CombinatorialData]
    public async Task InsertEntities_WithJson(InsertStrategy strategy)
    {
        // Arrange
        var entities = new List<TestEntityWithJson>
        {
            new TestEntityWithJson
            {
                TestRun = _run,
                JsonArray = [1],
                JsonObject = new OwnedObject { Code = 1, Name = "Test1" },
            },
            new TestEntityWithJson
            {
                TestRun = _run,
                JsonArray = [2],
                JsonObject = new OwnedObject { Code = 2, Name = "Test2" },
            },
        };

        // Act
        var insertedEntities = await _context.InsertWithStrategyAsync(strategy, entities);

        // Assert
        insertedEntities.Should().BeEquivalentTo(entities,
            o => o.RespectingRuntimeTypes().Excluding(e => e.Id));
    }

    [SkippableFact]
    public async Task InsertEntities_AndReturn_AsyncEnumerable()
    {
        Skip.If(_context.IsProvider(ProviderType.MySql));

        // Arrange
        var entities = new List<TestEntity>
        {
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity1" },
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity2" }
        };

        // Act
        var enumerable = _context.ExecuteBulkInsertReturnEnumerableAsync(entities);

        var insertedEntities = new List<TestEntity>();
        await foreach (var item in enumerable)
        {
            insertedEntities.Add(item);
        }

        // Assert
        insertedEntities.Should().BeEquivalentTo(entities,
            o => o.RespectingRuntimeTypes().Excluding(e => e.Id));
    }

    [SkippableTheory]
    [CombinatorialData]
    public async Task InsertEntities_MoveRows(InsertStrategy strategy)
    {
        Skip.If(_context.IsProvider(ProviderType.Oracle), "Unstable with Oracle");

        // Arrange
        var entities = new List<TestEntity>
        {
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity1" },
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity2" }
        };

        // Act
        var insertedEntities = await _context.InsertWithStrategyAsync(strategy, entities, o =>
        {
            o.MoveRows = true;
        });

        // Assert
        insertedEntities.Should().BeEquivalentTo(entities,
            o => o.RespectingRuntimeTypes().Excluding(e => e.Id));
    }

    [SkippableTheory]
    [CombinatorialData]
    public async Task InsertEntities_DoesNothing_WhenEntitiesAreEmpty(InsertStrategy strategy)
    {
        // Arrange
        var entities = new List<TestEntity>();

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await _context.InsertWithStrategyAsync(strategy, entities));

        // Assert
        var insertedEntities = _context.TestEntities.Where(x => x.TestRun == _run).ToList();
        Assert.Empty(insertedEntities);
    }

    [SkippableTheory]
    [CombinatorialData]
    public async Task InsertEntities_Many(InsertStrategy strategy)
    {
        // Arrange
        const int count = 56055;

        var entities = Enumerable.Range(1, count).Select(i => new TestEntity
        {
            Identifier = Guid.NewGuid(),
            TestRun = _run,
            Name = $"{_run}_Entity{i}",
            NumericEnumValue = (NumericEnum)(i % 2),
            Price = (decimal)(i * 0.1),
            StringEnumValue = (StringEnum)(i % 2),
        }).ToList();

        // Act
        var insertedEntities = await _context.InsertWithStrategyAsync(strategy, entities, o =>
        {
            o.MoveRows = false;
        });

        // Assert
        Assert.Equal(count, insertedEntities.Count);
        Assert.Contains(insertedEntities, e => e.Name == $"{_run}_Entity1");
        Assert.Contains(insertedEntities, e => e.Name == $"{_run}_Entity" + count);
    }

    [SkippableTheory]
    [CombinatorialData]
    public async Task InsertEntities_AndReturn_WithEntityWithValueConverters(InsertStrategy strategy)
    {
        // Arrange
        var now = DateTime.UtcNow;

        var entities = new List<TestEntityWithConverters>
        {
            new TestEntityWithConverters() { TestRun = _run, Name = $"{_run}_Entity1", CreatedAt = now, Uri = null },
            new TestEntityWithConverters() { TestRun = _run, Name = $"{_run}_Entity2", CreatedAt = now.AddDays(-1), Uri = new Uri("http://example.com/test") }
        };

        // Act
        var insertedEntities = await _context.InsertWithStrategyAsync(strategy, entities);

        // Assert
        insertedEntities.Should().BeEquivalentTo(entities,
            o => o.RespectingRuntimeTypes().Excluding(e => e.Id));
    }

    [SkippableTheory]
    [CombinatorialData]
    public async Task InsertEntities_WithOpenTransaction_CommitsSuccessfully(InsertStrategy strategy)
    {
        // Arrange
        var entities = new List<TestEntity>
        {
            new TestEntity { TestRun = _run, Name = $"{_run}_EntityWithTx1" },
            new TestEntity { TestRun = _run, Name = $"{_run}_EntityWithTx2" }
        };

        // Act
        await using var transaction = await _context.Database.BeginTransactionAsync();
        var insertedEntities = await _context.InsertWithStrategyAsync(strategy, entities);
        await transaction.CommitAsync();

        // Assert
        insertedEntities.Should().BeEquivalentTo(entities,
            o => o.RespectingRuntimeTypes().Excluding(e => e.Id));
    }

    [SkippableTheory]
    [CombinatorialData]
    public async Task InsertEntities_WithOpenTransaction_RollsBackOnFailure(InsertStrategy strategy)
    {
        // Oracle: the ReturnEntities / conflict paths use a temporary table created and dropped via DDL,
        // and DDL issues an implicit COMMIT, so the rows cannot be rolled back. See docs/limitations.md.
        Skip.If(_context.IsProvider(ProviderType.Oracle) && strategy is InsertStrategy.InsertReturn or InsertStrategy.InsertReturnAsync);

        // Arrange
        var entities = new List<TestEntity>
        {
            new TestEntity { TestRun = _run, Name = $"{_run}_EntityWithTxFail1" },
            new TestEntity { TestRun = _run, Name = $"{_run}_EntityWithTxFail2" }
        };

        await using var transaction = await _context.Database.BeginTransactionAsync();
        await _context.InsertWithStrategyAsync(strategy, entities);
        await transaction.RollbackAsync();

        // Assert
        _context.ChangeTracker.Clear();

        var insertedEntities = _context.TestEntities.Where(x => x.TestRun == _run).ToList();
        Assert.Empty(insertedEntities);
    }

    [SkippableTheory]
    [CombinatorialData]
    public async Task InsertEntities_WithOpenTransaction_MultipleInserts(InsertStrategy strategy)
    {
        // Oracle: ORA-39822: A new direct path operation is not allowed in the current transaction.
        Skip.If(_context.IsProvider(ProviderType.Oracle));

        // Arrange
        var entities = new List<TestEntity>
        {
            new TestEntity { TestRun = _run, Name = $"{_run}_EntityWithTx1" },
            new TestEntity { TestRun = _run, Name = $"{_run}_EntityWithTx2" },
            new TestEntity { TestRun = _run, Name = $"{_run}_EntityWithTx3" },
            new TestEntity { TestRun = _run, Name = $"{_run}_EntityWithTx4" },
        };

        // Act
        await using var transaction = await _context.Database.BeginTransactionAsync();

        var batches = entities.Chunk(2);
        foreach (var batch in batches)
        {
            await _context.InsertWithStrategyAsync(strategy, batch.ToList());
        }

        await transaction.CommitAsync();
    }

    [SkippableFact]
    public async Task ThrowsWhenUsingWrongConfigurationType()
    {
        // Skip for providers that don't support this feature
        Skip.If(_context.IsProvider(ProviderType.Sqlite));

        // Arrange
        var entities = new List<TestEntity>
        {
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity1" },
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity2" }
        };

        // Act & Assert
#if !NETCOREAPP1_0_OR_GREATER
        if (_context.IsProvider(ProviderType.SqlServer))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _context.ExecuteBulkInsertAsync(entities, (PhenX.EntityFrameworkCore.BulkInsert.MySql.MySqlBulkInsertOptions _) =>
                {
                }));
        }
#endif

        if (_context.IsProvider(ProviderType.MySql))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _context.ExecuteBulkInsertAsync(entities, (SqlServerBulkInsertOptions _) =>
                {
                }));
        }

        if (_context.IsProvider(ProviderType.PostgreSql))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _context.ExecuteBulkInsertAsync(entities, (SqlServerBulkInsertOptions _) =>
                {
                }));
        }
    }

    [SkippableTheory]
    [CombinatorialData]
    public async Task InsertEntities_WithGeneratedGuidId(InsertStrategy strategy)
    {
        // Arrange
        var entities = new List<TestEntityWithGuidId>
        {
            new TestEntityWithGuidId { TestRun = _run, Id = Guid.NewGuid(), Name = $"{_run}_Entity1" },
            new TestEntityWithGuidId { TestRun = _run, Id = Guid.NewGuid(), Name = $"{_run}_Entity2" }
        };

        // Act - Guid PKs should be included in INSERT without requiring CopyGeneratedColumns = true
        var insertedEntities = await _context.InsertWithStrategyAsync(strategy, entities);

        // Assert - all fields including the application-assigned Guid Id should be preserved
        insertedEntities.Should().BeEquivalentTo(entities,
            o => o.RespectingRuntimeTypes());
    }

    [SkippableTheory]
    [CombinatorialData]
    public async Task InsertEntities_RunsClientSideValueGenerator(InsertStrategy strategy)
    {
        // Arrange - Ids are left unset so the configured value generator must populate them.
        var entities = new List<TestEntityWithGeneratedGuidId>
        {
            new TestEntityWithGeneratedGuidId { TestRun = _run, Name = $"{_run}_Entity1" },
            new TestEntityWithGeneratedGuidId { TestRun = _run, Name = $"{_run}_Entity2" }
        };

        // Act
        var insertedEntities = await _context.InsertWithStrategyAsync(strategy, entities);

        // Assert - the value generator produced unique, non-empty keys (no PK violation)...
        insertedEntities.Should().HaveCount(2);
        insertedEntities.Should().OnlyContain(e => e.Id != Guid.Empty);
        insertedEntities.Select(e => e.Id).Should().OnlyHaveUniqueItems();

        // ...and the generated values were written back onto the source entities.
        entities.Should().OnlyContain(e => e.Id != Guid.Empty);
        entities.Select(e => e.Id).Should().BeEquivalentTo(insertedEntities.Select(e => e.Id));
    }

    [SkippableTheory]
    [CombinatorialData]
    public async Task InsertEntities_RunsValueGenerator_WithCopyGeneratedColumns(InsertStrategy strategy)
    {
        // Arrange
        var entities = new List<TestEntityWithGeneratedGuidId>
        {
            new TestEntityWithGeneratedGuidId { TestRun = _run, Name = $"{_run}_Entity1" },
            new TestEntityWithGeneratedGuidId { TestRun = _run, Name = $"{_run}_Entity2" }
        };

        // Act
        var insertedEntities = await _context.InsertWithStrategyAsync(strategy, entities,
            o => o.CopyGeneratedColumns = true);

        // Assert
        insertedEntities.Should().HaveCount(2);
        insertedEntities.Should().OnlyContain(e => e.Id != Guid.Empty);
        insertedEntities.Select(e => e.Id).Should().OnlyHaveUniqueItems();
        entities.Select(e => e.Id).Should().BeEquivalentTo(insertedEntities.Select(e => e.Id));
    }

    [SkippableTheory]
    [CombinatorialData]
    public async Task InsertEntities_ValueGenerator_PreservesExplicitlySetIds(InsertStrategy strategy)
    {
        // Arrange - when an Id is explicitly set (not the sentinel), the generator must not override it.
        var explicitId1 = TestHelpers.NewId();
        var explicitId2 = TestHelpers.NewId();
        var entities = new List<TestEntityWithGeneratedGuidId>
        {
            new TestEntityWithGeneratedGuidId { TestRun = _run, Id = explicitId1, Name = $"{_run}_Entity1" },
            new TestEntityWithGeneratedGuidId { TestRun = _run, Id = explicitId2, Name = $"{_run}_Entity2" }
        };

        // Act
        var insertedEntities = await _context.InsertWithStrategyAsync(strategy, entities);

        // Assert
        insertedEntities.Select(e => e.Id).Should().BeEquivalentTo([explicitId1, explicitId2]);
    }

    [SkippableTheory]
    [CombinatorialData]
    public async Task HandleProgress(InsertStrategy strategy)
    {
        // Arrange
        var entities = new List<TestEntity>
        {
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity1" },
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity2" },
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity3" },
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity4" },
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity5" },
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity6" },
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity7" },
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity8" },
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity9" },
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity10" },
        };

        long progressCount = 0;
        var callCount = 0;

        // Act
        await _context.InsertWithStrategyAsync(strategy, entities, o =>
        {
            o.NotifyProgressAfter = 2;
            o.OnProgress = count =>
            {
                progressCount = count;
                callCount++;
            };
        });

        // Assert
        Assert.Equal(10, progressCount);
        Assert.Equal(5, callCount);
    }

    [SkippableTheory]
    [CombinatorialData]
    public async Task HandleNoProgress(InsertStrategy strategy)
    {
        // Arrange
        var entities = new List<TestEntity>
        {
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity1" },
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity2" },
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity3" },
            new TestEntity { TestRun = _run, Name = $"{_run}_Entity4" },
        };

        var callCount = 0;

        // Act
        await _context.InsertWithStrategyAsync(strategy, entities, o =>
        {
            // NotifyProgressAfter not set, so no progress callback should be invoked
            o.OnProgress = _ => callCount++;
        });

        // Assert
        Assert.Equal(0, callCount);
    }

    [SkippableTheory]
    [CombinatorialData]
    public async Task InsertEntities_WithAllSimpleTypes(InsertStrategy strategy)
    {
        // Arrange
        var entities = new List<TestEntityWithSimpleTypes>
        {
            new TestEntityWithSimpleTypes
            {
                TestRun = _run,
                Id = 1,
                BoolValue = true,
                ByteValue = 1,
                ByteArrayValue =
                [
                    1,
                    2,
                    3
                ],
                SByteValue = -1,
                ShortValue = 2,
                IntValue = 3,
                LongValue = 4,
                FloatValue = 5.5f,
                DoubleValue = 6.6,
                DecimalValue = 7.7m,
                DateTimeValue = DateTime.UtcNow,
                DateTimeOffsetValue = DateTimeOffset.UtcNow,
                TimeSpanValue = TimeSpan.FromHours(1),
                StringValue = "Test String 2",
                CharValue = 'a',
                UShortValue = 10,
                UIntValue = 50,
                ULongValue = 200,
                DateOnlyValue = DateOnly.Parse("1985-10-31"),
                TimeOnlyValue = TimeOnly.Parse("12:00:00"),
                GuidValue = Guid.NewGuid(),
            },
            new TestEntityWithSimpleTypes
            {
                TestRun = _run,
                Id = 2,
                BoolValue = false,
                ByteValue = 10,
                ByteArrayValue =
                [
                    4,
                    5,
                    6
                ],
                SByteValue = -10,
                ShortValue = 20,
                IntValue = 30,
                LongValue = 40,
                FloatValue = 50.5f,
                DoubleValue = 60.6,
                DecimalValue = 70.7m,
                DateTimeValue = DateTime.UtcNow.AddDays(1),
                DateTimeOffsetValue = DateTimeOffset.UtcNow.AddDays(1),
                TimeSpanValue = TimeSpan.FromHours(2),
                StringValue = "Test String 2",
                CharValue = 'b',
                UShortValue = 50,
                UIntValue = 20,
                ULongValue = 100,
                DateOnlyValue = DateOnly.Parse("2023-10-01"),
                TimeOnlyValue = TimeOnly.Parse("12:00:00"),
                GuidValue = Guid.NewGuid(),
            }
        };

        // Act
        var insertedEntities = await _context.InsertWithStrategyAsync(strategy, entities);

        // Assert
        insertedEntities.Should()
            .BeEquivalentTo(entities, o => o
                .Using<DateTime>(ctx => ctx.Subject.Should().BeCloseTo(ctx.Expectation, 1.Seconds())).WhenTypeIs<DateTime>()
                .Using<DateTimeOffset>(ctx => ctx.Subject.Should().BeCloseTo(ctx.Expectation, 1.Seconds())).WhenTypeIs<DateTimeOffset>()
                .RespectingRuntimeTypes()
                .Excluding(e => e.Id)
            );
    }

    [SkippableTheory]
    [CombinatorialData]
    public async Task InsertsEntities_WithComplexType(InsertStrategy strategy)
    {        // Arrange
        var entities = new List<TestEntityWithComplexType>
        {
            new TestEntityWithComplexType
            {
                TestRun = _run,
                Id = 1,
                OwnedComplexType = new OwnedObject
                {
                    Code = 1,
                    Name = "Name1",
                }
            },
            new TestEntityWithComplexType
            {
                TestRun = _run,
                Id = 2,
                OwnedComplexType = new OwnedObject
                {
                    Code = 2,
                    Name = "Name2",
                }
            }
        };

        // Act
        var insertedEntities = await _context.InsertWithStrategyAsync(strategy, entities);

        // Assert
        insertedEntities.Should().BeEquivalentTo(entities,
            o => o.RespectingRuntimeTypes().Excluding(e => e.Id));
    }

    [SkippableFact]
    public async Task InsertEntities_WhenCancelled_DoesNotLeaveLockOrSession()
    {
        // MySql does not observe cancellation the same way; the bulk copy abort hook is provider-specific.
        Skip.If(_context.IsProvider(ProviderType.MySql));

        // Arrange - enough rows so that cancellation can trigger at a batch boundary before completion.
        const int count = 100_000;
        var entities = Enumerable.Range(1, count).Select(i => new TestEntity
        {
            TestRun = _run,
            Name = $"{_run}_Cancel{i}",
        }).ToList();

        using var cts = new CancellationTokenSource();

        // Act - cancel as soon as the first batch is reported, then ensure the operation is aborted.
        var act = async () =>
        {
            await _context.ExecuteBulkInsertAsync(entities, o =>
            {
                o.NotifyProgressAfter = 1;
                o.OnProgress = _ => cts.Cancel();
            }, cancellationToken: cts.Token);
        };

        await act.Should().ThrowAsync<OperationCanceledException>();

        _context.ChangeTracker.Clear();

        // Assert - a follow-up operation on the same table must complete promptly, proving that no
        // table lock / server session was left hanging by the cancelled direct-path / bulk load.
        using var followUpCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var followUp = new List<TestEntity>
        {
            new TestEntity { TestRun = _run, Name = $"{_run}_AfterCancel" },
        };

        await _context.ExecuteBulkInsertAsync(followUp, _ => { }, cancellationToken: followUpCts.Token);

        _context.ChangeTracker.Clear();
        var persisted = _context.TestEntities.Where(x => x.TestRun == _run && x.Name == $"{_run}_AfterCancel").ToList();
        persisted.Should().ContainSingle();
    }
}
