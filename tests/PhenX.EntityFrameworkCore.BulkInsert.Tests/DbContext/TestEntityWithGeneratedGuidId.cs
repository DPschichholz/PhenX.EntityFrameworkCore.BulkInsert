using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.DbContext;

/// <summary>
/// Entity whose primary key is populated by a client-side value generator
/// (<see cref="GuidV7ValueGenerator"/>) configured via the fluent API.
/// </summary>
[Table("test_entity_generated_guids")]
public class TestEntityWithGeneratedGuidId : TestEntityBase
{
    public Guid Id { get; set; }

    [Column("name")]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;
}


