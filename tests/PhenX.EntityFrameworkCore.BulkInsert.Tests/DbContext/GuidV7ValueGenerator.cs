using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.DbContext;

/// <summary>
/// Client-side value generator that produces time-ordered Guid values, used to verify that bulk insert
/// invokes value generators configured via <c>HasValueGenerator</c> + <c>ValueGeneratedOnAdd</c>.
/// </summary>
public class GuidV7ValueGenerator : ValueGenerator<Guid>
{
    public override bool GeneratesTemporaryValues => false;

    public override Guid Next(EntityEntry entry) => TestHelpers.NewId();
}

