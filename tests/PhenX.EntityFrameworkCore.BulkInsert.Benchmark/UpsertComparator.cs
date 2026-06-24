using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

using DotNet.Testcontainers.Containers;

using EFCore.BulkExtensions;

using Microsoft.EntityFrameworkCore;

using PhenX.EntityFrameworkCore.BulkInsert.Extensions;
using PhenX.EntityFrameworkCore.BulkInsert.Options;

namespace PhenX.EntityFrameworkCore.BulkInsert.Benchmark;

/// <summary>
/// Compares the conflict-resolution (upsert / merge) path of the supported libraries.
/// Half of the rows already exist (matched on the unique <see cref="UpsertTestEntity.Name"/>
/// column) so each run performs a mix of updates and inserts.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.ColdStart, launchCount: 1, warmupCount: 0, iterationCount: 5)]
public abstract class UpsertComparator
{
    [Params(100_000)]
    public int N;

    private IList<UpsertTestEntity> _data = [];
    protected TestDbContext DbContext { get; set; } = null!;

    protected UpsertComparator()
    {
        DbContainer = GetDbContainer();
        DbContainer?.StartAsync().GetAwaiter().GetResult();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        ConfigureDbContext();
        DbContext.Database.EnsureCreated();

        // Reset the table to a known state for persistent (containerized) databases.
        DbContext.UpsertEntities.ExecuteDelete();

        // Seed the first half so the upsert updates them; the second half will be inserted.
        var existing = Enumerable.Range(1, N / 2).Select(i => new UpsertTestEntity
        {
            Name = $"Entity{i}",
            Price = i,
        }).ToList();
        DbContext.ExecuteBulkInsert(existing);
        DbContext.ChangeTracker.Clear();

        // Incoming batch: the first half match existing rows (update), the rest are new (insert).
        _data = Enumerable.Range(1, N).Select(i => new UpsertTestEntity
        {
            Name = $"Entity{i}",
            Price = i * 2,
        }).ToList();
    }

    protected abstract void ConfigureDbContext();

    protected virtual string GetConnectionString()
    {
        return DbContainer?.GetConnectionString() ?? string.Empty;
    }

    private IDatabaseContainer? DbContainer { get; }

    protected abstract IDatabaseContainer? GetDbContainer();

    [Benchmark(Baseline = true)]
    public async Task PhenX_Upsert()
    {
        await DbContext.ExecuteBulkInsertAsync(_data, onConflict: new OnConflictOptions<UpsertTestEntity>
        {
            Match = e => new { e.Name },
            Update = (inserted, excluded) => new UpsertTestEntity { Price = excluded.Price },
        });
    }

    [Benchmark]
    public async Task PhenX_Upsert_Conditional()
    {
        await DbContext.ExecuteBulkInsertAsync(_data, onConflict: new OnConflictOptions<UpsertTestEntity>
        {
            Match = e => new { e.Name },
            Update = (inserted, excluded) => new UpsertTestEntity { Price = excluded.Price },
            // Only update when the incoming price is greater than the stored one.
            Where = (inserted, excluded) => excluded.Price > inserted.Price,
        });
    }

    [Benchmark]
    public async Task EFCore_BulkExtensions_InsertOrUpdate()
    {
        await DbContext.BulkInsertOrUpdateAsync(_data, new BulkConfig
        {
            UpdateByProperties = [nameof(UpsertTestEntity.Name)],
            PropertiesToIncludeOnUpdate = [nameof(UpsertTestEntity.Price)],
        });
    }

    [Benchmark]
    public async Task Z_EntityFramework_Extensions_BulkMerge()
    {
        await DbContext.BulkMergeAsync(_data, options =>
        {
            options.ColumnPrimaryKeyExpression = e => e.Name;
        });
    }
}

