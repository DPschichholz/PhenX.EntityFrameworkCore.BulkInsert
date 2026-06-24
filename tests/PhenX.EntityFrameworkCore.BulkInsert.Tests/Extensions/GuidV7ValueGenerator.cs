using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Extensions;

public sealed class GuidV7ValueGenerator : ValueGenerator<Guid>
{
	public override bool GeneratesTemporaryValues => false;
#if NET9_0_OR_GREATER
	public override Guid Next(EntityEntry _) => Guid.CreateVersion7();
#else
	public override Guid Next(EntityEntry _) => Guid.NewGuid();
#endif
}
