using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Converter;

public sealed class DateOnlyDateTimeConverter : ValueConverter<DateOnly, DateTime>
{
	public DateOnlyDateTimeConverter()
		: base(dateOnly => dateOnly.ToDateTime(TimeOnly.MinValue), dateTime => DateOnly.FromDateTime(dateTime))
	{
	}
}