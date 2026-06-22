using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using PhenX.EntityFrameworkCore.BulkInsert.Extensions;
using PhenX.EntityFrameworkCore.BulkInsert.Options;
using PhenX.EntityFrameworkCore.BulkInsert.Oracle;
using PhenX.EntityFrameworkCore.BulkInsert.Tests.Extensions;

using Testcontainers.Oracle;

using Xunit;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Tests.Merge;

[Trait("Category", "Integration")]
[Trait("Category", "TestContainer")]
public sealed class OnConflictUpdateExpressionCacheOracleIntegrationTests : IAsyncLifetime
{
	private readonly OracleContainer _oracleContainer;

	public OnConflictUpdateExpressionCacheOracleIntegrationTests()
	{
		_oracleContainer = new OracleBuilder("gvenzl/oracle-free:23.7-slim-faststart")
			.Build();
	}

	[Fact]
	public async Task BulkInsert_OnConflict_UsesGeneratedUpdateExpression()
	{
		// Arrange
		await using CacheIntegrationDbContext dbContext = CreateDbContext();
		await dbContext.Database.EnsureCreatedAsync(CancellationToken.None);

		var existing = new CacheIntegrationEntity
		{
			Id = 1,
			Name = "existing",
			Counter = 1
		};

		dbContext.Entities.Add(existing);
		await dbContext.SaveChangesAsync(CancellationToken.None);

		var conflictingEntity = new CacheIntegrationEntity
		{
			Id = 1,
			Name = "updated-by-excluded",
			Counter = 99
		};


		// Act
		await dbContext.Set<CacheIntegrationEntity>()
			.ExecuteBulkInsertAsync(
				new[] { conflictingEntity },
				onConflict: new OnConflictOptions<CacheIntegrationEntity>
				{
					Match = entity => new { entity.Id },
					Update = (inserted, excluded) => new { Name = excluded.Name, Counter = excluded.Counter },
				},
				cancellationToken: CancellationToken.None
			);

		CacheIntegrationEntity updated = await dbContext.Entities
			.AsNoTracking()
			.SingleAsync(entity => entity.Id == existing.Id, CancellationToken.None);

		// Assert
		updated.Name.Should().Be(conflictingEntity.Name);
		updated.Counter.Should().Be(conflictingEntity.Counter);
	}

	public async Task InitializeAsync()
	{
		await _oracleContainer.StartAsync();
	}

	public async Task DisposeAsync()
	{
		await _oracleContainer.DisposeAsync();
	}

	private CacheIntegrationDbContext CreateDbContext()
	{
		DbContextOptions options = new DbContextOptionsBuilder<CacheIntegrationDbContext>()
			.UseOracle(_oracleContainer.GetConnectionString())
			.UseBulkInsertOracle()
			.Options;

		return new CacheIntegrationDbContext(options);
	}

	private sealed class CacheIntegrationDbContext : Microsoft.EntityFrameworkCore.DbContext
	{
		public CacheIntegrationDbContext(DbContextOptions options)
			: base(options)
		{
		}

		public DbSet<CacheIntegrationEntity> Entities { get; set; } = null!;

		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			base.OnModelCreating(modelBuilder);
			modelBuilder.Entity<CacheIntegrationEntity>()
				.Property(entity => entity.Id)
				.ValueGeneratedOnAdd();
			modelBuilder.Entity<CacheIntegrationEntity>()
				.Property(entity => entity.Name)
				.HasMaxLength(256);

            modelBuilder.ApplyCustomConventions(Database);
		}

		protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
        	base.ConfigureConventions(configurationBuilder);
        	configurationBuilder.ApplyCustomTypeConventions(Database);
        }
	}

	private sealed class CacheIntegrationEntity
	{
		public int Id { get; set; }
		public string Name { get; set; } = string.Empty;
		public int Counter { get; set; }
	}
}





