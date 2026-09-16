using System;
using System.Data;
using System.Globalization;
using NSubstitute;
using Orleans.Tests.SqlUtils;
using Xunit;

namespace UnitTests.StorageTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Storage")]
[TestCategory("AdoNet"), TestCategory("Storage")]
public class DbExtensionsInt32ConversionTests
{
    [Fact]
    public void GetInt32_ConvertsByte()
    {
        using var context = CreateReader(typeof(byte), (byte)7);
        Assert.Equal(7, DbExtensions.GetInt32(context.Reader, "Value"));
        Assert.Equal(7, DbExtensions.GetNullableInt32(context.Reader, "Value"));
    }

    [Fact]
    public void GetInt32_ConvertsInt16()
    {
        using var context = CreateReader(typeof(short), (short)1234);
        Assert.Equal(1234, DbExtensions.GetInt32(context.Reader, "Value"));
        Assert.Equal(1234, DbExtensions.GetNullableInt32(context.Reader, "Value"));
    }

    [Fact]
    public void GetInt32_ConvertsInt64()
    {
        using var context = CreateReader(typeof(long), 1024L);
        Assert.Equal(1024, DbExtensions.GetInt32(context.Reader, "Value"));
        Assert.Equal(1024, DbExtensions.GetNullableInt32(context.Reader, "Value"));
    }

    [Fact]
    public void GetInt32_ConvertsDecimal()
    {
        using var context = CreateReader(typeof(decimal), 42m);
        Assert.Equal(42, DbExtensions.GetInt32(context.Reader, "Value"));
        Assert.Equal(42, DbExtensions.GetNullableInt32(context.Reader, "Value"));
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void GetInt32AndNullableInt32_PreserveBoundaryValues(int value)
    {
        using var intContext = CreateReader(typeof(int), value);
        using var longContext = CreateReader(typeof(long), (long)value);
        using var decimalContext = CreateReader(typeof(decimal), (decimal)value);

        Assert.Equal(value, DbExtensions.GetInt32(intContext.Reader, "Value"));
        Assert.Equal(value, DbExtensions.GetNullableInt32(intContext.Reader, "Value"));
        Assert.Equal(value, DbExtensions.GetInt32(longContext.Reader, "Value"));
        Assert.Equal(value, DbExtensions.GetNullableInt32(longContext.Reader, "Value"));
        Assert.Equal(value, DbExtensions.GetInt32(decimalContext.Reader, "Value"));
        Assert.Equal(value, DbExtensions.GetNullableInt32(decimalContext.Reader, "Value"));
    }

    [Theory]
    [InlineData((long)int.MinValue - 1)]
    [InlineData((long)int.MaxValue + 1)]
    public void GetInt32_ThrowsOnOverflow(long value)
    {
        using var context = CreateReader(typeof(long), value);
        using var decimalContext = CreateReader(typeof(decimal), (decimal)value);

        Assert.Throws<OverflowException>(() => DbExtensions.GetInt32(context.Reader, "Value"));
        Assert.Throws<OverflowException>(() => DbExtensions.GetNullableInt32(context.Reader, "Value"));
        Assert.Throws<OverflowException>(() => DbExtensions.GetInt32(decimalContext.Reader, "Value"));
        Assert.Throws<OverflowException>(() => DbExtensions.GetNullableInt32(decimalContext.Reader, "Value"));
    }

    [Fact]
    public void GetNullableInt32_ReturnsNullForDbNull()
    {
        using var context = CreateReader(typeof(int), DBNull.Value);
        Assert.Null(DbExtensions.GetNullableInt32(context.Reader, "Value"));
    }

    [Fact]
    public void GetInt32_ThrowsForDbNull()
    {
        using var context = CreateReader(typeof(int), DBNull.Value);
        Assert.Throws<InvalidCastException>(() => DbExtensions.GetInt32(context.Reader, "Value"));
    }

    [Fact]
    public void GetInt32AndNullableInt32_UseInvariantCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "~";
        using var context = CreateReader(typeof(string), "-42");
        using var localizedContext = CreateReader(typeof(string), "~42");

        try
        {
            CultureInfo.CurrentCulture = culture;

            Assert.Equal(-42, DbExtensions.GetInt32(context.Reader, "Value"));
            Assert.Equal(-42, DbExtensions.GetNullableInt32(context.Reader, "Value"));
            Assert.Throws<FormatException>(() => DbExtensions.GetInt32(localizedContext.Reader, "Value"));
            Assert.Throws<FormatException>(() => DbExtensions.GetNullableInt32(localizedContext.Reader, "Value"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void GetInt32AndNullableInt32_ThrowForInvalidFormat()
    {
        using var context = CreateReader(typeof(string), "not an integer");
        Assert.Throws<FormatException>(() => DbExtensions.GetInt32(context.Reader, "Value"));
        Assert.Throws<FormatException>(() => DbExtensions.GetNullableInt32(context.Reader, "Value"));
    }

    [Fact]
    public void GetInt32AndNullableInt32_ThrowForUnsupportedType()
    {
        using var context = CreateReader(typeof(Guid), Guid.Empty);
        Assert.Throws<InvalidCastException>(() => DbExtensions.GetInt32(context.Reader, "Value"));
        Assert.Throws<InvalidCastException>(() => DbExtensions.GetNullableInt32(context.Reader, "Value"));
    }

    [Fact]
    public void GetInt32AndNullableInt32_WrapMissingFieldException()
    {
        var record = Substitute.For<IDataRecord>();
        var cause = new IndexOutOfRangeException("Missing column");
        record.GetOrdinal("Missing").Returns(_ => throw cause);

        var requiredException = Assert.Throws<DataException>(() => DbExtensions.GetInt32(record, "Missing"));
        var nullableException = Assert.Throws<DataException>(() => DbExtensions.GetNullableInt32(record, "Missing"));

        Assert.Equal("Field 'Missing' not found in data record.", requiredException.Message);
        Assert.Same(cause, requiredException.InnerException);
        Assert.Equal("Field 'Missing' not found in data record.", nullableException.Message);
        Assert.Same(cause, nullableException.InnerException);
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
            Table.Columns.Add("Decoy", typeof(int));
            Table.Columns.Add("Value", valueType);
            Table.Rows.Add(-99, value);
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
