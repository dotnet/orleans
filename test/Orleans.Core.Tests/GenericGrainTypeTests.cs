using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.TypeSystem;
using Xunit;

namespace UnitTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
public class GenericGrainTypeTests
{
    [Fact]
    public void GenericGrainTypes_Construct_NullFormatter_ThrowWithExactParameterName()
    {
        var grainType = ParseGrainType();
        var interfaceType = ParseGrainInterfaceType();

        var grainException = Assert.Throws<ArgumentNullException>(() => grainType.Construct(null!, typeof(int)));
        Assert.Equal("formatter", grainException.ParamName);

        var interfaceException = Assert.Throws<ArgumentNullException>(() => interfaceType.Construct(null!, typeof(int)));
        Assert.Equal("formatter", interfaceException.ParamName);
    }

    [Fact]
    public void GenericGrainTypes_Construct_NullTypeArguments_ThrowBeforeConverterUse()
    {
        var converter = CreateTypeConverter();

        var grainException = Assert.Throws<ArgumentNullException>(() => ParseGrainType().Construct(converter, null!));
        Assert.Equal("typeArguments", grainException.ParamName);

        var interfaceException = Assert.Throws<ArgumentNullException>(() => ParseGrainInterfaceType().Construct(converter, null!));
        Assert.Equal("typeArguments", interfaceException.ParamName);
    }

    [Fact]
    public void GenericGrainTypes_GetArguments_NullConverter_ThrowWithExactParameterName()
    {
        var grainException = Assert.Throws<ArgumentNullException>(() => ParseGrainType().GetArguments(null!));
        Assert.Equal("converter", grainException.ParamName);

        var interfaceException = Assert.Throws<ArgumentNullException>(() => ParseGrainInterfaceType().GetArguments(null!));
        Assert.Equal("formatter", interfaceException.ParamName);
    }

    [Fact]
    public void GenericGrainTypes_ValidConstruction_RoundTripsArguments()
    {
        var converter = CreateTypeConverter();
        var grainType = ParseGrainType();
        var interfaceType = ParseGrainInterfaceType();

        var constructedGrain = grainType.Construct(converter, typeof(int));
        var constructedInterface = interfaceType.Construct(converter, typeof(int));

        Assert.Equal(1, constructedGrain.Arity);
        Assert.True(constructedGrain.IsConstructed);
        Assert.Equal([typeof(int)], constructedGrain.GetArguments(converter));
        Assert.Equal(grainType.GrainType, constructedGrain.GetUnconstructedGrainType().GrainType);

        Assert.Equal(1, constructedInterface.Arity);
        Assert.True(constructedInterface.IsConstructed);
        Assert.Equal([typeof(int)], constructedInterface.GetArguments(converter));
        Assert.Equal(interfaceType.Value, constructedInterface.GetGenericGrainType().Value);
    }

    private static GenericGrainType ParseGrainType()
    {
        Assert.True(GenericGrainType.TryParse(GrainType.Create("unit.test`1"), out var result));
        return result;
    }

    private static GenericGrainInterfaceType ParseGrainInterfaceType()
    {
        Assert.True(GenericGrainInterfaceType.TryParse(GrainInterfaceType.Create("unit.test.interface`1"), out var result));
        return result;
    }

    private static TypeConverter CreateTypeConverter() =>
        new(
            Array.Empty<ITypeConverter>(),
            Array.Empty<ITypeNameFilter>(),
            Array.Empty<ITypeFilter>(),
            Options.Create(new TypeManifestOptions { AllowAllTypes = true }),
            new CachedTypeResolver());
}
