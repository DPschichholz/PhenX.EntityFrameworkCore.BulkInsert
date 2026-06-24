using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;

using PhenX.EntityFrameworkCore.BulkInsert.Metadata;
using PhenX.EntityFrameworkCore.BulkInsert.Options;
using PhenX.EntityFrameworkCore.BulkInsert.Sqlite;
using PhenX.EntityFrameworkCore.BulkInsert.Tests.DbContext;

using Xunit;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Tests.Metadata;

/// <summary>
/// Container-free unit tests asserting that a client-side value generator which relies on the
/// <see cref="EntityEntry"/> surfaces as a clear <see cref="NotSupportedException"/> during bulk
/// insert (the change tracker is bypassed, so no entry is available).
/// </summary>
public class EntryAccessingValueGeneratorTests
{
    private sealed class EntryAccessingEntity
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class EntryAccessingValueGenerator : ValueGenerator<Guid>
    {
        public override bool GeneratesTemporaryValues => false;

        // Deliberately reads from the entry (e.g. to derive an Id from other properties),
        // which is not available during bulk insert.
        public override Guid Next(EntityEntry entry) => entry.Entity.GetHashCode() == 0 ? Guid.Empty : Guid.NewGuid();
    }

    private sealed class EntryAccessingDbContext : TestDbContextBase
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EntryAccessingEntity>(builder =>
            {
                builder.HasKey(e => e.Id);
                builder.Property(e => e.Id)
                    .ValueGeneratedOnAdd()
                    .HasValueGenerator<EntryAccessingValueGenerator>();
            });
        }
    }

    [Fact]
    public void GetValue_GeneratorAccessesEntry_ThrowsNotSupportedException()
    {
        using var context = new EntryAccessingDbContext
        {
            ConfigureOptions = options => options
                .UseSqlite("Data Source=:memory:")
                .UseBulkInsertSqlite()
        };

        var tableInfo = new MetadataProvider().GetTableInfo<EntryAccessingEntity>(context);
        var idColumn = tableInfo.GetColumns().Single(c => c.PropertyName == nameof(EntryAccessingEntity.Id));

        // Id left unset (sentinel), so the generator must run while reading the column.
        var entity = new EntryAccessingEntity { Name = "test" };

        var act = () => idColumn.GetValue(entity, new BulkInsertOptions());

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*EntityEntry*")
            .WithMessage($"*{nameof(EntryAccessingEntity.Id)}*");
    }
}


