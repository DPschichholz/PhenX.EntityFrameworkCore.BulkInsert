using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Converter;

/// <summary>
/// Der Converter stellt sicher, dass leere Strings als String mit einem Whitespace-Zeichen in der Datenbank
/// gespeichert werden und beim Lesen wieder zu leeren Strings in der Anwendung konvertiert werden.
/// Oracle behandelt leere Strings als NULL, daher wird ein Whitespace als Platzhalter verwendet.
/// </summary>
public static class EmptyStringConverter
{
	//TODO AK check if the configuration
	public static readonly ValueConverter<string?, string?> EmptyToSpace =
		new(
			// to provider (DB)
			v => string.IsNullOrEmpty(v) ? " " : v,
			// from provider (DB)
			v => v == " " ? string.Empty : v ?? string.Empty
		);
}