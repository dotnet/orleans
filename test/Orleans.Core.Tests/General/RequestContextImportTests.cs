using Orleans.Runtime;
using Xunit;

namespace UnitTests.General;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public sealed class RequestContextImportTests : IDisposable
{
    public RequestContextImportTests() => RequestContext.Clear();

    public void Dispose() => RequestContext.Clear();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportCopiesEntriesWithDefaultComparer(bool ignoreCase)
    {
        var value = new List<int> { 1 };
        var otherValue = new object();
        var contextData = new Dictionary<string, object>(
            ignoreCase ? StringComparer.OrdinalIgnoreCase : EqualityComparer<string>.Default)
        {
            ["value"] = value,
            ["other"] = otherValue,
        };
        RequestContext.Set("stale", new object());

        RequestContextExtensions.Import(contextData);

        Assert.Equal(2, RequestContext.Keys.Count());
        Assert.Null(RequestContext.Get("stale"));
        Assert.Same(value, RequestContext.Get("value"));
        Assert.Same(otherValue, RequestContext.Get("other"));
        Assert.Null(RequestContext.Get("VALUE"));

        var replacement = new object();
        contextData["value"] = replacement;
        contextData.Remove("other");
        contextData["source-only"] = new object();

        Assert.Same(value, RequestContext.Get("value"));
        Assert.Same(otherValue, RequestContext.Get("other"));
        Assert.Null(RequestContext.Get("source-only"));

        RequestContext.Set("VALUE", otherValue);
        Assert.Same(value, RequestContext.Get("value"));
        Assert.Same(otherValue, RequestContext.Get("VALUE"));
        Assert.Equal(3, RequestContext.Keys.Count());

        RequestContext.Remove("value");
        RequestContext.Set("context-only", new object());
        Assert.Same(replacement, contextData["value"]);
        Assert.False(contextData.ContainsKey("context-only"));
    }

    [Fact]
    public void ImportRetainsCaseDistinctKeys()
    {
        var lowerValue = new object();
        var upperValue = new object();
        var contextData = new Dictionary<string, object>
        {
            ["key"] = lowerValue,
            ["KEY"] = upperValue,
        };

        RequestContextExtensions.Import(contextData);

        Assert.Equal(2, RequestContext.Keys.Count());
        Assert.Same(lowerValue, RequestContext.Get("key"));
        Assert.Same(upperValue, RequestContext.Get("KEY"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportClearsExistingContext(bool useNull)
    {
        RequestContext.Set("value", new object());
        RequestContext.ReentrancyId = Guid.NewGuid();

        RequestContextExtensions.Import(useNull ? null : new Dictionary<string, object>());

        Assert.Empty(RequestContext.Entries);
        Assert.Equal(Guid.Empty, RequestContext.ReentrancyId);
    }
}
