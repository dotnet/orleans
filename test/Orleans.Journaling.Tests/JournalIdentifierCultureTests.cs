using System.Globalization;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class JournalIdentifierCultureTests
{
    [Fact]
    public void JournalId_CreateAndCompare_UsesOrdinalSemanticsUnderTurkishCulture()
    {
        using var culture = new CultureScope(CultureInfo.GetCultureInfo("tr-TR"));

        var journalId = JournalId.Create("Iıİi", "café/日志");

        Assert.Equal("I%C4%B1%C4%B0i/caf%C3%A9%2F%E6%97%A5%E5%BF%97", journalId.Value);
        Assert.Equal(new JournalId(journalId.Value), journalId);
        Assert.NotEqual(new JournalId("i%C4%B1%C4%B0i/caf%C3%A9%2F%E6%97%A5%E5%BF%97"), journalId);
        Assert.True(JournalId.Create("Iıİi").IsPrefixOf(journalId));
        Assert.False(JournalId.Create("I").IsPrefixOf(journalId));
    }

    [Fact]
    public void JournalMetadata_CopyProperties_PreservesOrdinalNamesUnderTurkishCulture()
    {
        using var culture = new CultureScope(CultureInfo.GetCultureInfo("tr-TR"));
        var properties = new Dictionary<string, string>
        {
            ["I"] = "latin-upper",
            ["i"] = "latin-lower",
            ["İ"] = "dotted-upper",
            ["ı"] = "dotless-lower",
            ["café"] = "non-ascii",
        };

        var copy = JournalMetadata.CopyProperties(properties);

        Assert.Equal(properties.Count, copy.Count);
        Assert.Equal("latin-upper", copy["I"]);
        Assert.Equal("latin-lower", copy["i"]);
        Assert.Equal("dotted-upper", copy["İ"]);
        Assert.Equal("dotless-lower", copy["ı"]);
        Assert.Equal("non-ascii", copy["café"]);
    }

    [Fact]
    public void JournalIdentifierInputs_WithNullCharacter_AreRejectedUnderTurkishCulture()
    {
        using var culture = new CultureScope(CultureInfo.GetCultureInfo("tr-TR"));

        var journalIdException = Assert.Throws<ArgumentException>(() => JournalId.Create("valid\0invalid"));
        var metadataException = Assert.Throws<ArgumentException>(
            () => JournalMetadata.CopyProperties(new Dictionary<string, string> { ["valid\0invalid"] = "value" }));

        Assert.Equal("firstSegment", journalIdException.ParamName);
        Assert.Equal("propertyName", metadataException.ParamName);
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _originalUICulture = CultureInfo.CurrentUICulture;

        public CultureScope(CultureInfo culture)
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _originalCulture;
            CultureInfo.CurrentUICulture = _originalUICulture;
        }
    }
}
