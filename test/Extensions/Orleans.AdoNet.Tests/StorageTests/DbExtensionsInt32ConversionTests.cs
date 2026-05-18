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
        using var context = CreateReader(typeof(byte), (byte)7);
        Assert.Equal(7, DbExtensions.GetInt32(context.Reader, "Value"));
    }

    [Fact]
    public void GetInt32_ConvertsInt16()
    {
        using var context = CreateReader(typeof(short), (short)1234);
        Assert.Equal(1234, DbExtensions.GetInt32(context.Reader, "Value"));
    }

    [Fact]
    public void GetInt32_ConvertsInt64()
    {
        using var context = CreateReader(typeof(long), 1024L);
        Assert.Equal(1024, DbExtensions.GetInt32(context.Reader, "Value"));
    }

    [Fact]
    public void GetInt32_ConvertsDecimal()
    {
        using var context = CreateReader(typeof(decimal), 42m);
        Assert.Equal(42, DbExtensions.GetInt32(context.Reader, "Value"));
    }

    [Fact]
    public void GetInt32_ThrowsOnOverflow()
    {
        using var context = CreateReader(typeof(long), (long)int.MaxValue + 1);
        Assert.Throws<OverflowException>(() => DbExtensions.GetInt32(context.Reader, "Value"));
    }

    [Fact]
    public void GetNullableInt32_ReturnsNullForDbNull()
    {
        using var context = CreateReader(typeof(int), DBNull.Value);
        Assert.Null(DbExtensions.GetNullableInt32(context.Reader, "Value"));
    }

    private static ReaderContext CreateReader(Type valueType, object value)
    {
        return new ReaderContext(valueType, value);
    }

    private sealed class ReaderContext : IDisposable
    {
        public ReaderContext(Type valueType, object value)
        {
            Table = new DataTable();
            Table.Columns.Add("Value", valueType);
            Table.Rows.Add(value);
            Reader = Table.CreateDataReader();
            Assert.True(Reader.Read());
        }

        public DataTable Table { get; }

        public DataTableReader Reader { get; }

        public void Dispose()
        {
            Reader.Dispose();
            Table.Dispose();
        }
    }
}
