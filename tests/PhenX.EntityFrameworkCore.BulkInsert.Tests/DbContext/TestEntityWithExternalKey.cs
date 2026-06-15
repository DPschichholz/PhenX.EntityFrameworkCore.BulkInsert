using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.DbContext;

/// <summary>
/// Entity with an application-assigned <see cref="Guid"/> primary key, while conflict matching is performed
/// on the separate <see cref="ExternalId"/> column that acts as an external (business) key.
/// </summary>
[Table("test_entity_external_keys")]
public class TestEntityWithExternalKey : TestEntityBase
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("external_id")]
    [MaxLength(100)]
    public string ExternalId { get; set; } = string.Empty;

    [Column("name")]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;
}

