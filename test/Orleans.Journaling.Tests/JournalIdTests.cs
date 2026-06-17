using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class JournalIdTests
{
    [Fact]
    public void Create_AcceptsReadOnlySpanSegments()
    {
        string[] segments = ["named", "logs", "segment/with spaces"];

        var journalId = JournalId.Create(segments.AsSpan());

        Assert.Equal("named/logs/segment%2Fwith%20spaces", journalId.Value);
    }

    [Fact]
    public void Create_AcceptsReadOnlySpanAdditionalSegments()
    {
        string[] additionalSegments = ["logs", "segment/with spaces"];

        var journalId = JournalId.Create("named", additionalSegments.AsSpan());

        Assert.Equal("named/logs/segment%2Fwith%20spaces", journalId.Value);
    }

    [Fact]
    public void Create_RejectsEmptyReadOnlySpanSegments()
    {
        var exception = Assert.Throws<ArgumentException>(() => JournalId.Create(ReadOnlySpan<string>.Empty));

        Assert.Equal("segments", exception.ParamName);
    }

    [Fact]
    public void Create_RejectsInvalidReadOnlySpanAdditionalSegments()
    {
        string[] additionalSegments = ["logs", ".."];

        var exception = Assert.Throws<ArgumentException>(() => JournalId.Create("named", additionalSegments.AsSpan()));

        Assert.Equal("additionalSegments", exception.ParamName);
    }
}
