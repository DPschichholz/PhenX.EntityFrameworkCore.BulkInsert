using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Converter;

public sealed class UtcValueConverter : ValueConverter<DateTimeOffset, DateTimeOffset>
{
	public UtcValueConverter()
		: base(d => d.ToUniversalTime(), d => d.ToLocalTime())
	{
	}
}