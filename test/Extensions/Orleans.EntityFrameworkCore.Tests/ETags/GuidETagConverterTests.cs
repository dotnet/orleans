using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.ETags;

[TestCategory(EFCoreTestCategories.Unit)]
public sealed class GuidETagConverterTests
{
    private const string ExpectedGuid = "a0b1c2d3-e4f5-4678-9123-456789abcdef";

    [Theory]
    [InlineData(ConverterKind.Clustering, ExpectedGuid)]
    [InlineData(ConverterKind.Clustering, "A0B1C2D3E4F546789123456789ABCDEF")]
    [InlineData(ConverterKind.GrainDirectory, ExpectedGuid)]
    [InlineData(ConverterKind.GrainDirectory, "A0B1C2D3E4F546789123456789ABCDEF")]
    [InlineData(ConverterKind.Persistence, ExpectedGuid)]
    [InlineData(ConverterKind.Persistence, "A0B1C2D3E4F546789123456789ABCDEF")]
    [InlineData(ConverterKind.Reminders, ExpectedGuid)]
    [InlineData(ConverterKind.Reminders, "A0B1C2D3E4F546789123456789ABCDEF")]
    public void ToDbETag_ParsesCanonicalAndNonCanonicalGuid(ConverterKind kind, string value)
    {
        var converter = CreateConverter(kind);

        var result = converter.ToDbETag(value);

        Assert.Equal(Guid.Parse(ExpectedGuid), result);
        Assert.NotEqual(Guid.Empty, result);
    }

    [Theory]
    [InlineData(ConverterKind.Clustering)]
    [InlineData(ConverterKind.GrainDirectory)]
    [InlineData(ConverterKind.Persistence)]
    [InlineData(ConverterKind.Reminders)]
    public void FromDbETag_UsesCanonicalDFormat(ConverterKind kind)
    {
        var converter = CreateConverter(kind);

        var result = converter.FromDbETag(Guid.Parse(ExpectedGuid.ToUpperInvariant()));

        Assert.Equal(ExpectedGuid, result);
        Assert.Equal(36, result.Length);
    }

    [Theory]
    [InlineData(ConverterKind.Clustering)]
    [InlineData(ConverterKind.GrainDirectory)]
    [InlineData(ConverterKind.Persistence)]
    [InlineData(ConverterKind.Reminders)]
    public void Converter_RoundTripsWithoutChangingTheGuid(ConverterKind kind)
    {
        var converter = CreateConverter(kind);
        var expected = Guid.Parse("12345678-90ab-cdef-1234-567890abcdef");

        var serialized = converter.FromDbETag(expected);
        var result = converter.ToDbETag(serialized);

        Assert.Equal(expected, result);
        Assert.Equal("12345678-90ab-cdef-1234-567890abcdef", serialized);
    }

    [Theory]
    [InlineData(ConverterKind.Clustering, "")]
    [InlineData(ConverterKind.Clustering, " ")]
    [InlineData(ConverterKind.Clustering, "not-a-guid")]
    [InlineData(ConverterKind.GrainDirectory, "")]
    [InlineData(ConverterKind.GrainDirectory, " ")]
    [InlineData(ConverterKind.GrainDirectory, "not-a-guid")]
    [InlineData(ConverterKind.Persistence, "")]
    [InlineData(ConverterKind.Persistence, " ")]
    [InlineData(ConverterKind.Persistence, "not-a-guid")]
    [InlineData(ConverterKind.Reminders, "")]
    [InlineData(ConverterKind.Reminders, " ")]
    [InlineData(ConverterKind.Reminders, "not-a-guid")]
    public void ToDbETag_MalformedValue_ThrowsFormatException(ConverterKind kind, string value)
    {
        var converter = CreateConverter(kind);

        Assert.Throws<FormatException>(() => converter.ToDbETag(value));
    }

    private static GuidConverter CreateConverter(ConverterKind kind) => kind switch
    {
        ConverterKind.Clustering => new(
            new GuidClusterETagConverter().ToDbETag,
            new GuidClusterETagConverter().FromDbETag),
        ConverterKind.GrainDirectory => new(
            new GuidGrainDirectoryETagConverter().ToDbETag,
            new GuidGrainDirectoryETagConverter().FromDbETag),
        ConverterKind.Persistence => new(
            new GuidGrainStorageETagConverter().ToDbETag,
            new GuidGrainStorageETagConverter().FromDbETag),
        ConverterKind.Reminders => new(
            new GuidReminderETagConverter().ToDbETag,
            new GuidReminderETagConverter().FromDbETag),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public enum ConverterKind
    {
        Clustering,
        GrainDirectory,
        Persistence,
        Reminders
    }

    private sealed record GuidConverter(Func<string, Guid> ToDbETag, Func<Guid, string> FromDbETag);
}
