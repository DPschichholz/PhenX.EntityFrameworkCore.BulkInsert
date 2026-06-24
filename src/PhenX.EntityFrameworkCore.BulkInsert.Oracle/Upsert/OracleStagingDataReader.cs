using System.Data;

using PhenX.EntityFrameworkCore.BulkInsert.Metadata;
using PhenX.EntityFrameworkCore.BulkInsert.Options;

namespace PhenX.EntityFrameworkCore.BulkInsert.Oracle.Upsert;

/// <summary>
/// An <see cref="IDataReader"/> over the entities that additionally exposes the technical
/// <see cref="OracleGttColumns.BatchId"/> and <see cref="OracleGttColumns.ClientRowId"/> columns,
/// so the whole batch can be streamed into the staging GTT in a single bulk copy.
/// </summary>
internal sealed class OracleStagingDataReader<T> : IDataReader where T : class
{
    private const int BatchIdOrdinal = 0;
    private const int ClientRowIdOrdinal = 1;
    private const int FirstPayloadOrdinal = 2;

    private readonly IEnumerator<T> _enumerator;
    private readonly IReadOnlyList<ColumnMetadata> _payload;
    private readonly BulkInsertOptions _options;
    private readonly byte[] _batchIdBytes;
    private readonly Dictionary<string, int> _ordinalMap;

    private long _clientRowId = -1;

    public OracleStagingDataReader(
        IEnumerable<T> rows,
        IReadOnlyList<ColumnMetadata> payload,
        Guid batchId,
        BulkInsertOptions options)
    {
        _enumerator = rows.GetEnumerator();
        _payload = payload;
        _options = options;

        // ODP.NET stores System.Guid as RAW(16) using Guid.ToByteArray() order; match it for BATCH_ID.
        _batchIdBytes = batchId.ToByteArray();

        _ordinalMap = new Dictionary<string, int>
        {
            [OracleGttColumns.BatchId] = BatchIdOrdinal,
            [OracleGttColumns.ClientRowId] = ClientRowIdOrdinal,
        };

        for (var i = 0; i < payload.Count; i++)
        {
            _ordinalMap[payload[i].PropertyName] = FirstPayloadOrdinal + i;
        }
    }

    public object GetValue(int i)
    {
        switch (i)
        {
            case BatchIdOrdinal:
                return _batchIdBytes;
            case ClientRowIdOrdinal:
                return _clientRowId;
            default:
                var current = _enumerator.Current;
                return current == null ? DBNull.Value : _payload[i - FirstPayloadOrdinal].GetValue(current, _options)!;
        }
    }

    public int GetValues(object[] values)
    {
        for (var i = 0; i < FieldCount; i++)
        {
            values[i] = GetValue(i);
        }

        return FieldCount;
    }

    public bool Read()
    {
        if (!_enumerator.MoveNext())
        {
            return false;
        }

        _clientRowId++;
        return true;
    }

    public Type GetFieldType(int i) => i switch
    {
        BatchIdOrdinal => typeof(byte[]),
        ClientRowIdOrdinal => typeof(long),
        _ => _payload[i - FirstPayloadOrdinal].ClrType,
    };

    public int GetOrdinal(string name) => _ordinalMap.GetValueOrDefault(name, -1);

    public int FieldCount => FirstPayloadOrdinal + _payload.Count;

    public int Depth => 0;

    public int RecordsAffected => 0;

    public bool IsClosed => false;

    public void Close()
    {
    }

    public void Dispose() => _enumerator.Dispose();

    public bool IsDBNull(int i) => GetValue(i) is DBNull;

    public DataTable GetSchemaTable() => throw new NotImplementedException();

    public bool NextResult() => throw new NotImplementedException();

    public object this[int i] => throw new NotImplementedException();

    public object this[string name] => throw new NotImplementedException();

    public string GetString(int i) => throw new NotImplementedException();

    public bool GetBoolean(int i) => throw new NotImplementedException();

    public byte GetByte(int i) => throw new NotImplementedException();

    public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length) => throw new NotImplementedException();

    public char GetChar(int i) => throw new NotImplementedException();

    public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length) => throw new NotImplementedException();

    public IDataReader GetData(int i) => throw new NotImplementedException();

    public string GetDataTypeName(int i) => throw new NotImplementedException();

    public DateTime GetDateTime(int i) => throw new NotImplementedException();

    public decimal GetDecimal(int i) => throw new NotImplementedException();

    public double GetDouble(int i) => throw new NotImplementedException();

    public float GetFloat(int i) => throw new NotImplementedException();

    public Guid GetGuid(int i) => throw new NotImplementedException();

    public short GetInt16(int i) => throw new NotImplementedException();

    public int GetInt32(int i) => throw new NotImplementedException();

    public long GetInt64(int i) => throw new NotImplementedException();

    public string GetName(int i) => throw new NotImplementedException();
}

