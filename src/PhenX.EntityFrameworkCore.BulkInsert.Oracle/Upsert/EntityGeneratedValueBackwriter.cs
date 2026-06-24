using Microsoft.EntityFrameworkCore;

using PhenX.EntityFrameworkCore.BulkInsert.Metadata;

namespace PhenX.EntityFrameworkCore.BulkInsert.Oracle.Upsert;

/// <summary>
/// Writes the generated ID (and any optional returned values) from the REF CURSOR result back onto
/// the originating entity instances, mapped strictly through the technical CLIENT_ROW_ID.
/// </summary>
internal sealed class EntityGeneratedValueBackwriter
{
    public void Write<T>(
        IReadOnlyList<T> entities,
        IReadOnlyList<UpsertResultRow> results,
        ColumnMetadata? idColumn,
        IReadOnlyList<ColumnMetadata> returnedColumns) where T : class
    {
        var idSetter = idColumn != null ? PropertyAccessor.CreateSetter(idColumn.Property.PropertyInfo!) : null;
        var returnedSetters = returnedColumns
            .Select(c => PropertyAccessor.CreateSetter(c.Property.PropertyInfo!))
            .ToList();

        foreach (var row in results)
        {
            if (row.ClientRowId < 0 || row.ClientRowId >= entities.Count)
            {
                throw new InvalidOperationException(
                    $"The Oracle upsert returned an out-of-range CLIENT_ROW_ID ({row.ClientRowId}) for entity '{typeof(T).Name}'.");
            }

            var entity = entities[(int)row.ClientRowId];

            if (idColumn != null && idSetter != null)
            {
                idSetter(entity, ConvertToClr(row.Id, idColumn));
            }

            for (var i = 0; i < returnedColumns.Count; i++)
            {
                returnedSetters[i](entity, ConvertToClr(row.ReturnedValues[i], returnedColumns[i]));
            }
        }
    }

    private static object? ConvertToClr(object? providerValue, ColumnMetadata column)
    {
        if (providerValue == null)
        {
            return null;
        }

        var targetType = Nullable.GetUnderlyingType(column.ClrType) ?? column.ClrType;

        var converter = column.Property.GetValueConverter() ?? column.Property.GetTypeMapping().Converter;
        if (converter != null && !targetType.IsInstanceOfType(providerValue))
        {
            providerValue = converter.ConvertFromProvider(providerValue);
        }

        if (providerValue == null || targetType.IsInstanceOfType(providerValue))
        {
            return providerValue;
        }

        // Oracle stores System.Guid as RAW(16); the REF CURSOR returns it as a byte[] (or a hex string)
        // for which EF has no ValueConverter, so it is reconstructed explicitly. ODP.NET uses the same
        // byte order as Guid.ToByteArray(), which new Guid(byte[]) reverses correctly.
        if (targetType == typeof(Guid))
        {
            switch (providerValue)
            {
                case byte[] bytes when bytes.Length == 16:
                    return new Guid(bytes);
                case string guidText:
                    return Guid.Parse(guidText);
            }
        }

        if (targetType == typeof(byte[]) && providerValue is byte[] rawBytes)
        {
            return rawBytes;
        }

        try
        {
            return Convert.ChangeType(providerValue, targetType, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            throw new InvalidOperationException(
                $"Cannot map the returned value '{providerValue}' ({providerValue.GetType().Name}) " +
                $"to property '{column.PropertyName}' of type '{targetType.Name}'.", ex);
        }
    }
}


