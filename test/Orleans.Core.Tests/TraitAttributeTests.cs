using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace UnitTests;

[TestCategory("BVT")]
[TestCategory("Testing")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Testing")]
public class TraitAttributeTests
{
    [Fact]
    public void SuiteDiscoverer_returns_suite_trait()
    {
        var discoverer = new SuiteDiscoverer(new NoOpMessageSink());

        var result = Assert.Single(discoverer.GetTraits(new TestAttributeInfo("BVT")));

        Assert.Equal(TestTraitNames.Suite, result.Key);
        Assert.Equal("BVT", result.Value);
    }

    [Fact]
    public void ProviderDiscoverer_returns_provider_trait()
    {
        var discoverer = new ProviderDiscoverer(new NoOpMessageSink());

        var result = Assert.Single(discoverer.GetTraits(new TestAttributeInfo("None")));

        Assert.Equal(TestTraitNames.Provider, result.Key);
        Assert.Equal("None", result.Value);
    }

    [Fact]
    public void AreaDiscoverer_returns_area_trait()
    {
        var discoverer = new AreaDiscoverer(new NoOpMessageSink());

        var result = Assert.Single(discoverer.GetTraits(new TestAttributeInfo("Streaming")));

        Assert.Equal(TestTraitNames.Area, result.Key);
        Assert.Equal("Streaming", result.Value);
    }

    [Fact]
    public void TestCategoryDiscoverer_returns_category_trait()
    {
        var discoverer = new CategoryDiscoverer(new NoOpMessageSink());

        var result = Assert.Single(discoverer.GetTraits(new TestAttributeInfo("Functional")));

        Assert.Equal(TestTraitNames.Category, result.Key);
        Assert.Equal("Functional", result.Value);
    }

    private sealed class NoOpMessageSink : LongLivedMarshalByRefObject, IMessageSink
    {
        public bool OnMessage(IMessageSinkMessage message) => true;
    }

    private sealed class TestAttributeInfo : LongLivedMarshalByRefObject, IAttributeInfo
    {
        private readonly object[] constructorArguments;

        public TestAttributeInfo(params object[] constructorArguments)
        {
            this.constructorArguments = constructorArguments;
        }

        public IEnumerable<object> GetConstructorArguments() => constructorArguments;

        public IEnumerable<IAttributeInfo> GetCustomAttributes(string assemblyQualifiedAttributeTypeName) =>
            Array.Empty<IAttributeInfo>();

        public TValue GetNamedArgument<TValue>(string argumentName) => throw new NotSupportedException();
    }
}
