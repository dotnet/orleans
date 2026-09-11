using System.Globalization;
using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.ETags;

[TestArea("EFCore")]
[TestProvider("None")]
[TestSuite("BVT")]
public sealed class SqlServerETagConverterTests
{
    [Theory]
    [InlineData(ConverterKind.Clustering, 0UL)]
    [InlineData(ConverterKind.Clustering, 1UL)]
    [InlineData(ConverterKind.Clustering, ulong.MaxValue)]
    [InlineData(ConverterKind.GrainDirectory, 0UL)]
    [InlineData(ConverterKind.GrainDirectory, 1UL)]
    [InlineData(ConverterKind.GrainDirectory, ulong.MaxValue)]
    [InlineData(ConverterKind.Persistence, 0UL)]
    [InlineData(ConverterKind.Persistence, 1UL)]
    [InlineData(ConverterKind.Persistence, ulong.MaxValue)]
    [InlineData(ConverterKind.Reminders, 0UL)]
    [InlineData(ConverterKind.Reminders, 1UL)]
    [InlineData(ConverterKind.Reminders, ulong.MaxValue)]
    public void Converter_RoundTripsUInt64AndUsesPlatformByteOrder(ConverterKind kind, ulong expected)
    {
        var converter = CreateConverter(kind);

        var databaseValue = converter.ToDbETag(expected.ToString());
        var result = converter.FromDbETag(databaseValue);

        Assert.Equal(BitConverter.GetBytes(expected), databaseValue);
        Assert.Equal(expected.ToString(), result);
        Assert.Equal(sizeof(ulong), databaseValue.Length);
    }

    [Theory]
    [InlineData(ConverterKind.Clustering)]
    [InlineData(ConverterKind.GrainDirectory)]
    [InlineData(ConverterKind.Persistence)]
    [InlineData(ConverterKind.Reminders)]
    public void ToDbETag_NonNumericValue_ThrowsFormatException(ConverterKind kind)
    {
        var converter = CreateConverter(kind);

        Assert.Throws<FormatException>(() => converter.ToDbETag("not-an-etag"));
    }

    [Theory]
    [InlineData(ConverterKind.Clustering)]
    [InlineData(ConverterKind.GrainDirectory)]
    [InlineData(ConverterKind.Persistence)]
    [InlineData(ConverterKind.Reminders)]
    public void FromDbETag_TooShort_ThrowsArgumentOutOfRangeException(ConverterKind kind)
    {
        var converter = CreateConverter(kind);

        Assert.Throws<ArgumentOutOfRangeException>(() => converter.FromDbETag([1, 2, 3, 4]));
    }

    [Theory]
    [InlineData(ConverterKind.Clustering)]
    [InlineData(ConverterKind.GrainDirectory)]
    [InlineData(ConverterKind.Persistence)]
    [InlineData(ConverterKind.Reminders)]
    public void Converter_UsesInvariantCulture(ConverterKind kind)
    {
        var converter = CreateConverter(kind);
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.PositiveSign = "p";
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture;

            Assert.Throws<FormatException>(() => converter.ToDbETag("p42"));
            Assert.Equal("42", converter.FromDbETag(BitConverter.GetBytes(42UL)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Theory]
    [InlineData(ConverterKind.Clustering)]
    [InlineData(ConverterKind.GrainDirectory)]
    [InlineData(ConverterKind.Persistence)]
    [InlineData(ConverterKind.Reminders)]
    public void FromDbETag_InvalidRowVersionLength_HasDeterministicMessage(ConverterKind kind)
    {
        var converter = CreateConverter(kind);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => converter.FromDbETag(new byte[sizeof(ulong) + 1]));

        Assert.Contains("exactly 8 bytes", exception.Message, StringComparison.Ordinal);
        Assert.Equal("etag", exception.ParamName);
    }

    private static ByteArrayConverter CreateConverter(ConverterKind kind) => kind switch
    {
        ConverterKind.Clustering => new(
            new Orleans.Clustering.EntityFrameworkCore.SqlServer.SqlServerClusterETagConverter().ToDbETag,
            new Orleans.Clustering.EntityFrameworkCore.SqlServer.SqlServerClusterETagConverter().FromDbETag),
        ConverterKind.GrainDirectory => new(
            new Orleans.GrainDirectory.SqlServerGrainDirectoryETagConverter().ToDbETag,
            new Orleans.GrainDirectory.SqlServerGrainDirectoryETagConverter().FromDbETag),
        ConverterKind.Persistence => new(
            new Orleans.Persistence.SqlServerGrainStateETagConverter().ToDbETag,
            new Orleans.Persistence.SqlServerGrainStateETagConverter().FromDbETag),
        ConverterKind.Reminders => new(
            new Orleans.Reminders.SqlServerReminderETagConverter().ToDbETag,
            new Orleans.Reminders.SqlServerReminderETagConverter().FromDbETag),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public enum ConverterKind
    {
        Clustering,
        GrainDirectory,
        Persistence,
        Reminders
    }

    private sealed record ByteArrayConverter(Func<string, byte[]> ToDbETag, Func<byte[], string> FromDbETag);
}
