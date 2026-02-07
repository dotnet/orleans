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
        var context = CreateReader(typeof(byte), (byte)7);
        using (context.Table)
        using (context.Reader)
        {
            Assert.Equal(7, DbExtensions.GetInt32(context.Reader, "Value"));
        }
    }

    [Fact]
    public void GetInt32_ConvertsInt16()
    {
        var context = CreateReader(typeof(short), (short)1234);
        using (context.Table)
        using (context.Reader)
        {
            Assert.Equal(1234, DbExtensions.GetInt32(context.Reader, "Value"));
        }
    }

    [Fact]
    public void GetInt32_ConvertsInt64()
    {
        var context = CreateReader(typeof(long), 1024L);
        using (context.Table)
        using (context.Reader)
        {
            Assert.Equal(1024, DbExtensions.GetInt32(context.Reader, "Value"));
        }
    }

    [Fact]
    public void GetInt32_ConvertsDecimal()
    {
        var context = CreateReader(typeof(decimal), 42m);
        using (context.Table)
        using (context.Reader)
        {
            Assert.Equal(42, DbExtensions.GetInt32(context.Reader, "Value"));
        }
    }

    [Fact]
    public void GetInt32_ThrowsOnOverflow()
    {
        var context = CreateReader(typeof(long), (long)int.MaxValue + 1);
        using (context.Table)
        using (context.Reader)
        {
            Assert.Throws<OverflowException>(() => DbExtensions.GetInt32(context.Reader, "Value"));
        }
    }

    [Fact]
    public void GetNullableInt32_ReturnsNullForDbNull()
    {
        var context = CreateReader(typeof(int), DBNull.Value);
        using (context.Table)
        using (context.Reader)
        {
            Assert.Null(DbExtensions.GetNullableInt32(context.Reader, "Value"));
        }
    }

    private static (DataTable Table, DataTableReader Reader) CreateReader(Type valueType, object value)
    {
        var table = new DataTable();
        table.Columns.Add("Value", valueType);
        table.Rows.Add(value);
        var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        return (table, reader);
    }
}
