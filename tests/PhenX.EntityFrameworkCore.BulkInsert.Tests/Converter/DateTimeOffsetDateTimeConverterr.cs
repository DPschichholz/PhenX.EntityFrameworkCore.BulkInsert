using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Converter;

public sealed class DateTimeOffsetDateTimeConverter : ValueConverter<DateTimeOffset, DateTime>
{
	public DateTimeOffsetDateTimeConverter()
		: base(
			d => d.LocalDateTime,
			d => new DateTimeOffset(d.ToUniversalTime())
		)
	{
	}
}