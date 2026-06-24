using PhenX.EntityFrameworkCore.BulkInsert.Metadata;
using PhenX.EntityFrameworkCore.BulkInsert.Options;

namespace PhenX.EntityFrameworkCore.BulkInsert.Oracle.Upsert;

/// <summary>
/// Verifies that a batch does not contain duplicate match keys before it is staged. Oracle
/// <c>MERGE</c> raises ORA-30926 ("unable to get a stable set of rows") when the same target row is
/// touched by more than one source row, so the duplicates must be rejected up front with a clear
/// error.
/// </summary>
internal sealed class DuplicateKeyValidator
{
    public void Validate<T>(
        Type entityType,
        IReadOnlyList<T> entities,
        IReadOnlyList<ColumnMetadata> matchColumns,
        BulkInsertOptions options) where T : class
    {
        if (matchColumns.Count == 0)
        {
            throw new InvalidOperationException("An upsert requires at least one match column.");
        }

        var seen = new HashSet<UpsertKeyValue>();

        foreach (var entity in entities)
        {
            var values = new object?[matchColumns.Count];
            for (var i = 0; i < matchColumns.Count; i++)
            {
                var value = matchColumns[i].GetValue(entity, options);
                values[i] = value is DBNull ? null : value;
            }

            var key = new UpsertKeyValue(values);
            if (!seen.Add(key))
            {
                var keyText = string.Join(", ", matchColumns.Select((c, i) => $"{c.PropertyName}={values[i] ?? "NULL"}"));
                throw new InvalidOperationException(
                    $"The batch for entity '{entityType.Name}' contains duplicate match keys ({keyText}). " +
                    "Oracle MERGE cannot update the same target row more than once; deduplicate the batch first.");
            }
        }
    }
}

/// <summary>
/// Value-equality wrapper over a (possibly composite) match key, honoring any value conversions that
/// already happened while reading the column values.
/// </summary>
internal readonly struct UpsertKeyValue : IEquatable<UpsertKeyValue>
{
    private readonly object?[] _values;

    public UpsertKeyValue(object?[] values) => _values = values;

    public bool Equals(UpsertKeyValue other)
    {
        if (_values.Length != other._values.Length)
        {
            return false;
        }

        for (var i = 0; i < _values.Length; i++)
        {
            if (!Equals(_values[i], other._values[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is UpsertKeyValue other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in _values)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }
}

