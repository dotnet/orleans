using Microsoft.Extensions.Logging.Abstractions;
using Orleans.AzureUtils;
using Orleans.Configuration;
using Xunit;

namespace Tester.AzureUtils;

[TestCategory("BVT"), TestCategory("AzureStorage"), TestCategory("Storage"), TestCategory("AzureQueue")]
[TestSuite("BVT")]
[TestProvider("AzureStorage")]
[TestArea("Persistence")]
public class AzureQueueNameTests
{
    public static IEnumerable<TheoryDataRow<string, string>> ValidNames =>
    [
        new("queue", "queue") { TestDisplayName = "Constructor_ValidName_LowercaseIdentity" },
        new("OrLeAnSQueue", "orleansqueue") { TestDisplayName = "Constructor_ValidName_MixedCase" },
        new("A/B\\C#D?E&F+G:H.I%J", "a-b-c-d-e-f-g-h-i-j") { TestDisplayName = "Constructor_ValidName_ExistingPunctuationSanitation" },
        new("1a-2b-3", "1a-2b-3") { TestDisplayName = "Constructor_ValidName_DigitsAndHyphens" },
        new("Ab1", "ab1") { TestDisplayName = "Constructor_ValidName_MinimumLength" },
        new(new string('A', 63), new string('a', 63)) { TestDisplayName = "Constructor_ValidName_MaximumLength" },
    ];

    [Theory, MemberData(nameof(ValidNames))]
    public void Constructor_ValidName_PreservesSanitizedQueueName(string queueName, string expectedName)
    {
        var manager = new AzureQueueDataManager(NullLoggerFactory.Instance, queueName, new AzureQueueOptions());

        Assert.Equal(expectedName, manager.QueueName);
    }

    [Theory]
    [InlineData(InvalidQueueNameCase.BelowMinimumLength)]
    [InlineData(InvalidQueueNameCase.AboveMaximumLength)]
    [InlineData(InvalidQueueNameCase.LeadingHyphen)]
    [InlineData(InvalidQueueNameCase.TrailingHyphen)]
    [InlineData(InvalidQueueNameCase.AdjacentHyphens)]
    [InlineData(InvalidQueueNameCase.UnsupportedUnderscore)]
    [InlineData(InvalidQueueNameCase.UnsupportedExclamation)]
    [InlineData(InvalidQueueNameCase.UnsupportedSpace)]
    [InlineData(InvalidQueueNameCase.NonAsciiLetterBeginning)]
    [InlineData(InvalidQueueNameCase.NonAsciiLetterInterior)]
    [InlineData(InvalidQueueNameCase.NonAsciiLetterEnd)]
    [InlineData(InvalidQueueNameCase.NonAsciiDigitBeginning)]
    [InlineData(InvalidQueueNameCase.NonAsciiDigitInterior)]
    [InlineData(InvalidQueueNameCase.NonAsciiDigitEnd)]
    public void Constructor_InvalidName_ThrowsWithQueueNameParameter(InvalidQueueNameCase testCase)
    {
        // Keep Unicode out of theory arguments and report names, as well as source text.
        var queueName = testCase switch
        {
            InvalidQueueNameCase.BelowMinimumLength => "ab",
            InvalidQueueNameCase.AboveMaximumLength => new string('a', 64),
            InvalidQueueNameCase.LeadingHyphen => "-abc",
            InvalidQueueNameCase.TrailingHyphen => "abc-",
            InvalidQueueNameCase.AdjacentHyphens => "ab--cd",
            InvalidQueueNameCase.UnsupportedUnderscore => "ab_cd",
            InvalidQueueNameCase.UnsupportedExclamation => "ab!cd",
            InvalidQueueNameCase.UnsupportedSpace => "ab cd",
            InvalidQueueNameCase.NonAsciiLetterBeginning => "\u00C9ab",
            InvalidQueueNameCase.NonAsciiLetterInterior => "a\u00C9b",
            InvalidQueueNameCase.NonAsciiLetterEnd => "ab\u00C9",
            InvalidQueueNameCase.NonAsciiDigitBeginning => "\u0661ab",
            InvalidQueueNameCase.NonAsciiDigitInterior => "a\u0661b",
            InvalidQueueNameCase.NonAsciiDigitEnd => "ab\u0661",
            _ => throw new ArgumentOutOfRangeException(nameof(testCase)),
        };

        var exception = Assert.Throws<ArgumentException>(
            () => new AzureQueueDataManager(NullLoggerFactory.Instance, queueName, new AzureQueueOptions()));

        Assert.Equal("queueName", exception.ParamName);
    }

    public enum InvalidQueueNameCase
    {
        BelowMinimumLength,
        AboveMaximumLength,
        LeadingHyphen,
        TrailingHyphen,
        AdjacentHyphens,
        UnsupportedUnderscore,
        UnsupportedExclamation,
        UnsupportedSpace,
        NonAsciiLetterBeginning,
        NonAsciiLetterInterior,
        NonAsciiLetterEnd,
        NonAsciiDigitBeginning,
        NonAsciiDigitInterior,
        NonAsciiDigitEnd,
    }
}
