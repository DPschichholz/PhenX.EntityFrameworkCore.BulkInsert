using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Converter;

/// <summary>
/// Konvertiert bool-Werte zu "J"/"N"-Strings in der Datenbank und umgekehrt.
/// </summary>
public sealed class JaNeinToBoolConverter : ValueConverter<bool, string>
{
	public JaNeinToBoolConverter()
		: base(
			v => v ? "J" : "N",
			v => v == "J"
		)
	{
	}
}
