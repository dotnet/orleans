using Xunit;
namespace UnitTests;

[TestCategory("BVT")]
[TestCategory("Testing")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Testing")]
public class TraitAttributeTests
{
    [Fact]
    public void TestSuiteAttribute_returns_suite_trait()
    {
        var attribute = new TestSuiteAttribute("BVT");

        var result = Assert.Single(attribute.GetTraits());

        Assert.Equal(TestTraitNames.Suite, result.Key);
        Assert.Equal("BVT", result.Value);
    }

    [Fact]
    public void TestProviderAttribute_returns_provider_trait()
    {
        var attribute = new TestProviderAttribute("None");

        var result = Assert.Single(attribute.GetTraits());

        Assert.Equal(TestTraitNames.Provider, result.Key);
        Assert.Equal("None", result.Value);
    }

    [Fact]
    public void TestAreaAttribute_returns_area_trait()
    {
        var attribute = new TestAreaAttribute("Streaming");

        var result = Assert.Single(attribute.GetTraits());

        Assert.Equal(TestTraitNames.Area, result.Key);
        Assert.Equal("Streaming", result.Value);
    }

    [Fact]
    public void TestCategoryAttribute_returns_category_trait()
    {
        var attribute = new TestCategoryAttribute("Functional");

        var result = Assert.Single(attribute.GetTraits());

        Assert.Equal(TestTraitNames.Category, result.Key);
        Assert.Equal("Functional", result.Value);
    }
}
