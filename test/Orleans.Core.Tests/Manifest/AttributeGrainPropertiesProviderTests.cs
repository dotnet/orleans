using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;
using Orleans.Runtime;
using Xunit;

namespace UnitTests.Manifest;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT"), TestCategory("Manifest")]
public sealed class AttributeGrainPropertiesProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullGrainClassPreservesExistingProperties(bool bindings)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = CreateProvider(services, bindings);
        var properties = new Dictionary<string, string> { ["existing"] = "value" };

        var exception = Assert.Throws<ArgumentNullException>(() => provider.Populate(null!, default, properties));

        Assert.Equal("grainClass", exception.ParamName);
        Assert.Equal("value", Assert.Single(properties).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullPropertyCollectionIsRejectedForUnannotatedClass(bool bindings)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var provider = CreateProvider(services, bindings);

        var exception = Assert.Throws<ArgumentNullException>(() => provider.Populate(typeof(object), default, null!));

        Assert.Equal("properties", exception.ParamName);
    }

    [Fact]
    public void PropertyAttributesReceiveServicesAndOriginalDerivedClass()
    {
        using var services = new ServiceCollection().AddSingleton("service-marker").BuildServiceProvider();
        var properties = new Dictionary<string, string> { ["existing"] = "value" };
        var grainType = GrainType.Create("test.grain");

        new AttributeGrainPropertiesProvider(services).Populate(typeof(DerivedGrain), grainType, properties);

        Assert.Equal(4, properties.Count);
        Assert.Equal("value", properties["existing"]);
        Assert.Equal("service-marker", properties["service"]);
        Assert.Equal(nameof(DerivedGrain), properties["class"]);
        Assert.Equal("test.grain", properties["grain"]);
    }

    [Fact]
    public void BindingAttributesPreserveGroupingAndDefaultMapperSentinel()
    {
        using var services = new ServiceCollection().AddSingleton("service-marker").BuildServiceProvider();
        var properties = new Dictionary<string, string> { ["existing"] = "value" };

        new AttributeGrainBindingsProvider(services).Populate(typeof(DerivedGrain), GrainType.Create("test.grain"), properties);

        Assert.Equal(5, properties.Count);
        Assert.Equal("value", properties["existing"]);
        Assert.Equal("service-marker", properties["binding.attr-1.type"]);
        Assert.Null(properties["binding.attr-1.mapper"]);
        Assert.Equal(nameof(DerivedGrain), properties["binding.attr-2.type"]);
        Assert.Equal("test.grain", properties["binding.attr-2.grain"]);
    }

    private static IGrainPropertiesProvider CreateProvider(IServiceProvider services, bool bindings)
        => bindings ? new AttributeGrainBindingsProvider(services) : new AttributeGrainPropertiesProvider(services);

    [TestProperties, TestBindings]
    private class BaseGrain { }
    private sealed class DerivedGrain : BaseGrain { }

    [AttributeUsage(AttributeTargets.Class, Inherited = true)]
    private sealed class TestPropertiesAttribute : Attribute, IGrainPropertiesProviderAttribute
    {
        public void Populate(IServiceProvider services, Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        {
            properties["service"] = services.GetRequiredService<string>();
            properties["class"] = grainClass.Name;
            properties["grain"] = grainType.ToString();
        }
    }

    [AttributeUsage(AttributeTargets.Class, Inherited = true)]
    private sealed class TestBindingsAttribute : Attribute, IGrainBindingsProviderAttribute
    {
        public IEnumerable<Dictionary<string, string?>> GetBindings(IServiceProvider services, Type grainClass, GrainType grainType)
        {
            yield return new() { ["type"] = services.GetRequiredService<string>(), ["mapper"] = null };
            yield return new() { ["type"] = grainClass.Name, ["grain"] = grainType.ToString() };
        }
    }
}
