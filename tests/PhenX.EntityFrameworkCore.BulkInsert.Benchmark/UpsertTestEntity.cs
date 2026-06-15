using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Microsoft.EntityFrameworkCore;

namespace PhenX.EntityFrameworkCore.BulkInsert.Benchmark;

/// <summary>
/// Entity used by the upsert/merge benchmark. The <see cref="Name"/> column carries a unique
/// constraint so it can be used as the conflict-matching key across all providers (PostgreSQL
/// and SQLite require a unique index for ON CONFLICT, Oracle uses it in the MERGE ON clause).
/// </summary>
[PrimaryKey(nameof(Id))]
[Index(nameof(Name), IsUnique = true)]
[Table(nameof(UpsertTestEntity))]
public class UpsertTestEntity
{
    public int Id { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public decimal Price { get; set; }
}

