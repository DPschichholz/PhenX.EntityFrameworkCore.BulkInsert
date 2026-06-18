using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Microsoft.EntityFrameworkCore;

namespace PhenX.EntityFrameworkCore.BulkInsert.Tests.DbContext;

/// <summary>
/// Wide entity (~23 payload columns of mixed types) used to exercise the Oracle GTT upsert pipeline
/// with a realistic, large payload. The primary key is database generated (identity) and the upsert
/// matches on the natural <see cref="Code"/> key.
/// </summary>
[PrimaryKey(nameof(Id))]
[Index(nameof(Code), IsUnique = true)]
[Table("oracle_wide_upsert")]
public class OracleWideUpsertEntity
{
    public long Id { get; set; }

    [Column("code")]
    [MaxLength(100)]
    public string Code { get; set; } = string.Empty;

    [Column("col_string_1")] [MaxLength(200)] public string? String1 { get; set; }
    [Column("col_string_2")] [MaxLength(200)] public string? String2 { get; set; }
    [Column("col_string_3")] [MaxLength(200)] public string? String3 { get; set; }
    [Column("col_string_4")] [MaxLength(200)] public string? String4 { get; set; }
    [Column("col_string_5")] [MaxLength(200)] public string? String5 { get; set; }

    [Column("col_int_1")] public int Int1 { get; set; }
    [Column("col_int_2")] public int Int2 { get; set; }
    [Column("col_int_3")] public int? Int3 { get; set; }

    [Column("col_long_1")] public long Long1 { get; set; }
    [Column("col_long_2")] public long? Long2 { get; set; }

    [Column("col_decimal_1")] public decimal Decimal1 { get; set; }
    [Column("col_decimal_2")] public decimal? Decimal2 { get; set; }

    [Column("col_double_1")] public double Double1 { get; set; }

    [Column("col_bool_1")] public bool Bool1 { get; set; }
    [Column("col_bool_2")] public bool? Bool2 { get; set; }

    [Column("col_date_1")] public DateTime Date1 { get; set; }
    [Column("col_date_2")] public DateTime? Date2 { get; set; }

    [Column("col_guid_1")] public Guid Guid1 { get; set; }
    [Column("col_guid_2")] public Guid? Guid2 { get; set; }

    [Column("col_short_1")] public short Short1 { get; set; }

    [Column("col_byte_1")] public byte Byte1 { get; set; }
}

