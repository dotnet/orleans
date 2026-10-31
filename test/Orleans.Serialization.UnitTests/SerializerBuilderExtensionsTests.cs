using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Configuration;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class SerializerBuilderExtensionsTests
{
    [Fact]
    public void ConfigureFactoryRejectsNullBuilder()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => SerializerBuilderExtensions.Configure(null!, _ => new ConfigureOptions<TypeManifestOptions>(_ => { })));

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void ConfigureInstanceRejectsNullBuilder()
    {
        var configuration = new ConfigureOptions<TypeManifestOptions>(_ => { });

        var exception = Assert.Throws<ArgumentNullException>(
            () => SerializerBuilderExtensions.Configure(null!, configuration));

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void ConfigureActionRejectsNullBuilder()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => SerializerBuilderExtensions.Configure(null!, (TypeManifestOptions _) => { }));

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void AddAssemblyRejectsNullBuilderBeforeInspectingAssembly()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => SerializerBuilderExtensions.AddAssembly(null!, null!));

        Assert.Equal("builder", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ConfigurePreservesBuilderAndAppliesRegisteredOptions(int overload)
    {
        var services = new ServiceCollection();
        services.AddSerializer(builder =>
        {
            void Configure(TypeManifestOptions options) => options.WellKnownTypeIds[123456] = typeof(SerializerBuilderExtensionsTests);
            var configuration = new ConfigureOptions<TypeManifestOptions>(Configure);
            var result = overload switch
            {
                0 => builder.Configure(_ => configuration),
                1 => builder.Configure(configuration),
                2 => builder.Configure((Action<TypeManifestOptions>)Configure),
                _ => throw new ArgumentOutOfRangeException(nameof(overload)),
            };

            Assert.Same(builder, result);
        });
        using var provider = services.BuildServiceProvider();

        var manifest = provider.GetRequiredService<IOptions<TypeManifestOptions>>().Value;

        Assert.Equal(typeof(SerializerBuilderExtensionsTests), manifest.WellKnownTypeIds[123456]);
    }

    [Fact]
    public void AddAssemblyWithoutManifestProvidersPreservesBuilderAndRegistrations()
    {
        var services = new ServiceCollection();
        services.AddSerializer(builder =>
        {
            var count = services.Count;

            var result = builder.AddAssembly(typeof(object).Assembly);

            Assert.Same(builder, result);
            Assert.Equal(count, services.Count);
        });
    }
}
