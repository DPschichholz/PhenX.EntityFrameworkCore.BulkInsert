using System.Reflection;

using Microsoft.EntityFrameworkCore;

using PhenX.EntityFrameworkCore.BulkInsert.Tests.Attributes;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Wrapper;

[PrimaryKey(nameof(Id))]
public sealed record EnumWrapper<TEnum> where TEnum : struct, Enum
{
	public TEnum Id { get; private init; }
	public string Name { get; private init; } = null!;
	public string? Description { get; private init; }

	public static IEnumerable<EnumWrapper<TEnum>> Seed()
	{
		var type = typeof(TEnum);
		var values = Enum.GetValues<TEnum>();

		foreach (var value in values)
		{
			// Initialize cache
			value.GetStringValue();
			var field = type.GetField(value.ToString())!;
			var attribute = field.GetCustomAttribute<StringValueAttribute>(inherit: true);
			yield return new EnumWrapper<TEnum>
			{
				Id = value,
				Name = value.ToString(),
				Description = attribute?.StringValue
			};
		}
	}
}
