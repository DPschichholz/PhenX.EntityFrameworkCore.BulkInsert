using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.ValueGeneration;

using PhenX.EntityFrameworkCore.BulkInsert.Dialect;
using PhenX.EntityFrameworkCore.BulkInsert.Options;

namespace PhenX.EntityFrameworkCore.BulkInsert.Metadata;

internal sealed class ColumnMetadata
{
    public ColumnMetadata(IProperty property,  SqlDialectBuilder dialect, IComplexProperty? complexProperty = null)
    {
        StoreObjectIdentifier? ownerTable = complexProperty != null
            ? StoreObjectIdentifier.Table(complexProperty.DeclaringType.GetTableName()!, complexProperty.DeclaringType.GetSchema())
            : null;

        _getter = BuildGetter(property, complexProperty);
        Property = property;
        PropertyName = property.Name;
        ColumnName = ownerTable == null ? property.GetColumnName() : property.GetColumnName(ownerTable.Value)!;
        QuotedColumName = dialect.Quote(ColumnName);
        StoreType = property.GetRelationalTypeMapping().StoreType;
        IsNullable = property.IsNullable;
        StoreDefinition = GetStoreDefinition(property);
        ClrType = property.ClrType;

        // A client-side value generator (configured via HasValueGenerator + ValueGeneratedOnAdd) is
        // normally invoked by EF Core during SaveChanges. Bulk insert bypasses the change tracker,
        // so the generator is executed here, while reading the column value, for any entity whose
        // current value still equals the sentinel (i.e. it was not explicitly set by the caller).
        TryBuildValueGenerator(property, complexProperty);

        IsGenerated = _valueGenerator == null
                      && property.ValueGenerated != ValueGenerated.Never
                      && (property.GetDefaultValueSql() != null
                          || property.GetComputedColumnSql() != null
                          || property.FindAnnotation(RelationalAnnotationNames.DefaultValue) != null
                          || (property.ClrType != typeof(Guid) && property.ClrType != typeof(Guid?)));
    }

    private readonly Func<object, object?> _getter;

    private ValueGenerator? _valueGenerator;
    private Func<object, object?>? _rawGetter;
    private Action<object, object?>? _setter;
    private object? _sentinel;

    public IProperty Property { get; }

    public string PropertyName { get; }

    public string ColumnName { get; }

    public string QuotedColumName { get; }

    public string StoreDefinition { get; }

    /// <summary>
    /// The provider store type without nullability (e.g. "NUMBER(19)", "VARCHAR2(200)").
    /// </summary>
    public string StoreType { get; }

    /// <summary>
    /// Whether the mapped column is nullable in the target table.
    /// </summary>
    public bool IsNullable { get; }

    public Type ClrType { get; }

    public bool IsGenerated { get; }

    public object GetValue(object entity, BulkInsertOptions options)
    {
        EnsureGeneratedValue(entity);

        var result = _getter(entity);

        if (options.Converters != null && result != null)
        {
            foreach (var converter in options.Converters)
            {
                if (converter.TryConvertValue(result, options, out var temp))
                {
                    result = temp;
                    break;
                }
            }
        }

        return result ?? DBNull.Value;
    }

    private void EnsureGeneratedValue(object entity)
    {
        if (_valueGenerator == null)
        {
            return;
        }

        var current = _rawGetter!(entity);
        if (!Equals(current, _sentinel))
        {
            return;
        }

        // Bulk insert bypasses the change tracker, so there is no real EntityEntry to pass.
        // Generators that only produce a value (e.g. Guid/sequence generators) ignore the entry
        // and work fine. Generators that read other properties via the entry are not supported
        // and surface as a clear NotSupportedException instead of a raw NullReferenceException.
        object? generated;
        try
        {
            generated = _valueGenerator.Next(null!);
        }
        catch (NullReferenceException ex)
        {
            throw new NotSupportedException(
                $"The value generator '{_valueGenerator.GetType().Name}' configured for property " +
                $"'{PropertyName}' accesses the EntityEntry, which is not available during bulk insert " +
                "(the change tracker is bypassed). Use a generator that does not rely on the entry, or " +
                "assign the value explicitly before inserting.", ex);
        }

        // Writing the value back makes repeated reads of the same row stable (some providers
        // read each column more than once) and reflects the generated value on the source entity.
        _setter!(entity, generated);
    }

    private void TryBuildValueGenerator(IProperty property, IComplexProperty? complexProperty)
    {
        // Only client-side generators explicitly configured via HasValueGenerator are supported.
        // Database-generated values and EF's implicit default selectors are not handled here.
        if (complexProperty != null
            || property.ValueGenerated == ValueGenerated.Never
            || property.PropertyInfo?.SetMethod == null)
        {
            return;
        }

        var factory = property.GetValueGeneratorFactory();
        if (factory == null)
        {
            return;
        }

        _valueGenerator = factory(property, property.DeclaringType);
        _rawGetter = PropertyAccessor.CreateGetter(property.PropertyInfo);
        _setter = PropertyAccessor.CreateSetter(property.PropertyInfo);
        _sentinel = property.Sentinel;
    }

    private static Func<object, object?> BuildGetter(IProperty property, IComplexProperty? complexProperty)
    {
        var valueConverter =
            property.GetValueConverter() ??
            property.GetTypeMapping().Converter;

        var propInfo = property.PropertyInfo!;

        return PropertyAccessor.CreateGetter(propInfo, complexProperty, valueConverter?.ConvertToProviderExpression);
    }

    private static string GetStoreDefinition(IProperty property)
    {
        var typeMapping = property.GetRelationalTypeMapping();

        var nullability = property.IsNullable ? "NULL" : "NOT NULL";

        return $"{typeMapping.StoreType} {nullability}";
    }

    public override string ToString()
    {
        return $"Name: {PropertyName}, Column: {ColumnName}";
    }
}
