using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Converter;

public sealed class UtcNullableValueConverter : ValueConverter<DateTimeOffset?, DateTimeOffset?>
{
	public UtcNullableValueConverter()
		: base(
			d => d.HasValue ? d.Value.ToUniversalTime() : null,
			d => d.HasValue ? d.Value.ToLocalTime() : null
		)
	{
	}
}


