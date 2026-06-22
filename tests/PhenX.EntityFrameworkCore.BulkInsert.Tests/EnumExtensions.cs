using System.Collections.Concurrent;
using System.Reflection;

using PhenX.EntityFrameworkCore.BulkInsert.Tests.Attributes;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests;

public static class EnumExtensions
{
	/// <summary>
	///     Converts a flagged enum into an array of enum values. If the flagged enum has a zero value it will always be
	///     contained in the output.
	/// </summary>
	/// <param name="value"></param>
	/// <typeparam name="T"></typeparam>
	/// <returns></returns>
	public static IEnumerable<T> FromFlags<T>(this T value) where T : struct, Enum
		=> Enum.GetValues<T>()
		   .Where(t => value.HasFlag(t));

	/// <summary>
	/// Get Display Name from Enum
	/// </summary>
	/// <param name="value">enum value</param>
	/// <returns>string value of enum display name</returns>
	public static string GetStringValue<TEnum>(this TEnum value) where TEnum : struct, Enum
	{
		// Caching
		var stringValue = s_cachedEnumNames.GetOrAdd(
			value,
			x =>
			{
				string fieldName = x.ToString();
				var fieldInfo = x.GetType().GetField(fieldName)!;

				var stringValueAttribute = fieldInfo.GetCustomAttributes<StringValueAttribute>(inherit: false)
				   .FirstOrDefault();
				return stringValueAttribute is not null ? stringValueAttribute.StringValue : fieldName;
			}
		);
		return stringValue;
	}

	private static readonly ConcurrentDictionary<Enum, string> s_cachedEnumNames = new();

	public static short GetPercentValue<TEnum>(this TEnum value) where TEnum : struct, Enum
	{
		// Caching
		var percent = s_cachedEnumPercentValues.GetOrAdd(
			value,
			x =>
			{
				string fieldName = x.ToString();
				var fieldInfo = x.GetType().GetField(fieldName)!;

				var stringValueAttribute = fieldInfo.GetCustomAttributes<PercentValueAttribute>(inherit: false)
				   .FirstOrDefault();
				return stringValueAttribute?.PercentValue ?? 0;
			}
		);
		return percent;
	}

	private static readonly ConcurrentDictionary<Enum, short> s_cachedEnumPercentValues = new();

}
