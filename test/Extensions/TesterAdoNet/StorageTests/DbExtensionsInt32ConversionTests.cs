using System;
using System.Data;
using Orleans.Tests.SqlUtils;
using Xunit;

namespace UnitTests.StorageTests;

[TestCategory("AdoNet"), TestCategory("Storage")]
public class DbExtensionsInt32ConversionTests
{
    [Fact]
    public void GetInt32_ConvertsByte()
    {
        using var reader = CreateReader(typeof(byte), (byte)7);
        Assert.Equal(7, DbExtensions.GetInt32(reader, "Value"));
    }

    [Fact]
    public void GetInt32_ConvertsInt16()
    {
        using var reader = CreateReader(typeof(short), (short)1234);
        Assert.Equal(1234, DbExtensions.GetInt32(reader, "Value"));
    }

    [Fact]
    public void GetInt32_ConvertsInt64()
    {
        using var reader = CreateReader(typeof(long), 1024L);
        Assert.Equal(1024, DbExtensions.GetInt32(reader, "Value"));
    }

    [Fact]
    public void GetInt32_ConvertsDecimal()
    {
        using var reader = CreateReader(typeof(decimal), 42m);
        Assert.Equal(42, DbExtensions.GetInt32(reader, "Value"));
    }

    [Fact]
    public void GetInt32_ThrowsOnOverflow()
    {
        using var reader = CreateReader(typeof(long), (long)int.MaxValue + 1);
        Assert.Throws<OverflowException>(() => DbExtensions.GetInt32(reader, "Value"));
    }

    [Fact]
    public void GetNullableInt32_ReturnsNullForDbNull()
    {
        using var reader = CreateReader(typeof(int), DBNull.Value);
        Assert.Null(DbExtensions.GetNullableInt32(reader, "Value"));
    }

    private static DataTableReader CreateReader(Type valueType, object value)
    {
        var table = new DataTable();
        table.Columns.Add("Value", valueType);
        table.Rows.Add(value);
        var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        return reader;
    }
}
