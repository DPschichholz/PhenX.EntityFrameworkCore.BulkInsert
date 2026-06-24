using System.Data;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;

using PhenX.EntityFrameworkCore.BulkInsert.Tests.Converter;
using PhenX.EntityFrameworkCore.BulkInsert.Tests.Wrapper;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.Extensions;

public static class ModelBuilderExtensions
{
	// TODO: prefix über Namespace ermitteln
	public static ModelBuilder ApplyCustomConventions(
		this ModelBuilder modelBuilder,
		DatabaseFacade database,
		string prefix = ""
	)
	{
		foreach (var entityType in modelBuilder.Model.GetEntityTypes())
		{
			if (entityType.IsAbstract() && entityType.GetMappingStrategy() is "TPC")
			{
				continue;
			}

			foreach (var property in entityType.GetProperties())
			{
				if (property.ClrType == typeof(string)
					&& !property.IsKey()
					&& !property.IsForeignKey()
					&& !property.IsConcurrencyToken)
				{
					property.SetValueConverter(EmptyStringConverter.EmptyToSpace);
				}

				if (property.ClrType == typeof(string)
					&& (string.Equals(property.GetColumnType(), "text", StringComparison.OrdinalIgnoreCase)
						|| property.GetMaxLength() is -1))
				{
					property.SetMaxLength(null);

					if (database.IsOracle())
					{
						property.SetColumnType("CLOB");
					}
					else if (database.IsNpgsql())
					{
						property.SetColumnType("text");
					}
				}

				if (property.ClrType == typeof(Guid) && property.ValueGenerated is ValueGenerated.OnAdd)
				{
					property.SetValueGeneratorFactory((_, _) => new GuidV7ValueGenerator());
				}

				property.SetColumnName(ToSnakeUpperCase(property.Name));
			}

			if (entityType.IsTableExcludedFromMigrations()
				|| (entityType.GetMappingStrategy() is "TPH" && entityType.ClrType.IsAbstract is false))
			{
				continue;
			}

			if (!entityType.ClrType.IsGenericType
				|| entityType.ClrType.GetGenericTypeDefinition() != typeof(EnumWrapper<>))
			{
				var typename = entityType.DisplayName();
				var tableName = ToSnakeUpperCase(prefix + typename);
				entityType.SetTableName(tableName);
				CreateForeignKeysForEnums(modelBuilder, entityType);
			}
		}

		if (database.IsOracle())
		{
			modelBuilder.UseRegexIsMatchSql();
		}

		return modelBuilder;
	}

	private static void CreateForeignKeysForEnums(ModelBuilder modelBuilder, IReadOnlyTypeBase entityType)
	{
		var enumProps = entityType.ClrType.GetProperties()
		   .Where(p => p.PropertyType.IsEnum && p.PropertyType.GetCustomAttribute<FlagsAttribute>() is null);

		foreach (var prop in enumProps)
		{
			var enumWrapperType = typeof(EnumWrapper<>).MakeGenericType(prop.PropertyType);
			var enumWrapperEntityType = modelBuilder.Model.FindEntityType(enumWrapperType);

			if (enumWrapperEntityType is not null)
			{
				modelBuilder.Entity(entityType.ClrType)
				   .HasOne(enumWrapperType)
				   .WithMany()
				   .HasForeignKey(prop.Name)
				   .HasPrincipalKey("Id");
			}
		}
	}

	public static ModelConfigurationBuilder ApplyCustomTypeConventions(
		this ModelConfigurationBuilder configurationBuilder,
		DatabaseFacade database
	)
	{
		configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcValueConverter>();
		configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<UtcNullableValueConverter>();

		if (database.IsNpgsql())
		{
			configurationBuilder
			   .Properties<byte[]>()
			   .HaveColumnType("bytea");
			configurationBuilder
			   .Properties<DateTimeOffset>()
			   .HaveColumnType("timestamp with time zone");
			configurationBuilder
			   .Properties<string>()
			   .HaveMaxLength(4000);
		}
		else if (database.IsOracle())
		{
			configurationBuilder
			   .Properties<byte[]>()
			   .HaveColumnType("BLOB");
			configurationBuilder
			   .Properties<DateTimeOffset>()
			   .HaveColumnType("TIMESTAMP(7) WITH TIME ZONE");
			configurationBuilder
			   .Properties<bool>()
			   .HaveColumnType("NUMBER(1)");
			configurationBuilder
			   .Properties<string>()
			   .AreUnicode(false);
			configurationBuilder
			   .Properties<DateOnly>()
			   .HaveConversion<DateOnlyDateTimeConverter>();
		}
		else if (database.ProviderName is not "Microsoft.EntityFrameworkCore.InMemory")
		{
			throw new NotSupportedException($"Database provider {database.ProviderName} is not supported.");
		}

		return configurationBuilder;
	}

	private static string ToSnakeUpperCase(string name, int maxLength = 120)
	{
		if (string.IsNullOrEmpty(name)) return name;

		var snakeCase = ConvertToSnakeUpperCase(name);

		if (snakeCase.Length > maxLength)
		{
			snakeCase = ShortenName(snakeCase, maxLength);
		}

		return snakeCase;
	}

	private static string ConvertToSnakeUpperCase(string name)
	{
		var result = new StringBuilder();
		var previousChar = char.MinValue;

		result.Append(char.ToUpperInvariant(name[0]));

		for (var i = 1; i < name.Length; i++)
		{
			var currentChar = name[i];

			if (ShouldInsertUnderscore(
					currentChar,
					previousChar,
					name,
					i
				))
			{
				result.Append('_');
			}

			result.Append(char.ToUpperInvariant(currentChar));
			previousChar = currentChar;
		}

		return result.ToString();
	}

	private static bool ShouldInsertUnderscore(
		char currentChar,
		char previousChar,
		string name,
		int currentIndex
	)
	{
		// Füge Unterstrich ein, wenn:
		// 1. Aktuelles Zeichen ist Großbuchstabe und vorheriges ist kein Unterstrich
		// und vorheriges ist kein Großbuchstabe (oder wir sind am Anfang eines neuen Wortes)
		var isTransitionToUpperCase = char.IsUpper(currentChar)
			&& previousChar != '_'
			&& (currentIndex > 1 && !char.IsUpper(name[currentIndex - 1]) || char.IsDigit(name[currentIndex - 1]));

		// 2. Aktuelles Zeichen ist eine Zahl und vorheriges ist keine Zahl und kein Unterstrich
		var isTransitionToDigit = char.IsDigit(currentChar) && !char.IsDigit(previousChar) && previousChar != '_';

		return isTransitionToUpperCase || isTransitionToDigit;
	}

	private static string ShortenName(string name, int maxLength)
	{
		// Zuerst versuchen, Vokale zu entfernen
		var shortened = RemoveVowels(name, maxLength);

		// Wenn immer noch zu lang, einfach abschneiden
		if (shortened.Length > maxLength)
		{
			shortened = shortened[..maxLength];
		}

		return shortened;
	}

	private static string RemoveVowels(string name, int maxLength)
	{
		var result = new StringBuilder(name);

		// Entferne Vokale von rechts nach links, aber nicht am Anfang oder Ende von Wörtern
		for (var i = result.Length - 1; i >= 0 && result.Length > maxLength; i--)
		{
			var c = result[i];
			if (IsVowel(c) && !IsAtWordBoundary(result, i))
			{
				result.Remove(i, 1);
			}
		}

		return result.ToString();
	}

	private static bool IsVowel(char c) => char.ToUpperInvariant(c) is 'A' or 'E' or 'I' or 'O' or 'U';

	private static bool IsAtWordBoundary(StringBuilder text, int index)
	{
		return index == 0
			|| index == text.Length - 1
			|| text[index - 1] == '_'
			|| text[index + 1] == '_';
	}

	public static void CreateEnumTable<TEnum>(
		this ModelBuilder modelBuilder,
		string prefix,
		bool isExcludedFromMigrations = false
	)
		where TEnum : struct, Enum
	{
		var data = EnumWrapper<TEnum>.Seed().ToArray();
		var typename = prefix + typeof(TEnum).Name + "Enum";
		var tableName = ToSnakeUpperCase(typename);

		var entity = modelBuilder.Entity<EnumWrapper<TEnum>>();
		entity
		   .ToTable(tableName)
		   .Metadata.SetIsTableExcludedFromMigrations(isExcludedFromMigrations);

		if (isExcludedFromMigrations is false)
		{
			entity.HasData(data);
		}

		entity
		   .Property(e => e.Name)
		   .HasMaxLength(data.Max(d => d.Name.Length));
		entity
		   .Property(e => e.Description)
		   .HasMaxLength(255);
		//TODO schwierigkeiten mit unterscheidlicher datenbank configuration.
		//Es kann char oder byte als default wert hinlegt werden.
		//Bei Wörtern mit umlauten haben wir mehr bytes als der string character repräsentiert
	}

	private static void UseRegexIsMatchSql(this ModelBuilder modelBuilder)
	{
		var methodInfo = typeof(Regex).GetMethod(nameof(Regex.IsMatch), [typeof(string), typeof(string)])!;

		var intMapping = new IntTypeMapping("NUMBER(10)", DbType.Int32);

		modelBuilder
		   .HasDbFunction(methodInfo)
		   .HasTranslation(args =>
				{
					var input = args[0];
					var pattern = args[1];

					// REGEXP_COUNT(input, pattern) > 0 ist äquivalent zu REGEXP_LIKE(input, pattern)
					return new SqlBinaryExpression(
						ExpressionType.GreaterThan,
						new SqlFunctionExpression(
							"REGEXP_COUNT",
							[input, pattern],
							nullable: true,
							argumentsPropagateNullability: [true, true],
							type: typeof(int),
							typeMapping: intMapping
						),
#if NET8_0
						new SqlConstantExpression(Expression.Constant(0, typeof(int)), intMapping),
#else
						new SqlConstantExpression(0, typeof(int), intMapping),
#endif
						typeof(bool),
						typeMapping: null
					);
				}
			);
	}
}
