using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Serializers;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class SerializerConfigurationAnalyzerTests
{
    [Fact]
    public void NullCodecProviderIsRejectedForEmptyManifest()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            SerializerConfigurationAnalyzer.AnalyzeSerializerAvailability(null!, new TypeManifestOptions()));

        Assert.Equal("codecProvider", exception.ParamName);
    }

    [Fact]
    public void NullOptionsReportsManifestParameter()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var codecProvider = services.GetRequiredService<ICodecProvider>();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            SerializerConfigurationAnalyzer.AnalyzeSerializerAvailability(codecProvider, null!));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void SupportedInterfaceParametersAndResultsHaveNoComplaints()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var options = new TypeManifestOptions();
        options.AddInterface(typeof(ISupportedInterface));

        var result = SerializerConfigurationAnalyzer.AnalyzeSerializerAvailability(
            services.GetRequiredService<ICodecProvider>(), options);

        Assert.Empty(result);
    }

    [Fact]
    public void MissingSerializersCollectEachReferencingInterfaceMethod()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var options = new TypeManifestOptions();
        options.AddInterface(typeof(IUnsupportedInterface));

        var result = SerializerConfigurationAnalyzer.AnalyzeSerializerAvailability(
            services.GetRequiredService<ICodecProvider>(), options);

        var complaint = Assert.Single(result);
        Assert.Equal(typeof(UnconfiguredValue), complaint.Key);
        Assert.False(complaint.Value.HasSerializer);
        Assert.False(complaint.Value.HasCopier);
        var methods = Assert.Single(complaint.Value.Methods);
        Assert.Equal(typeof(IUnsupportedInterface), methods.Key);
        Assert.Contains(typeof(IUnsupportedInterface).GetMethod(nameof(IUnsupportedInterface.Read))!, methods.Value);
        Assert.Contains(typeof(IUnsupportedInterface).GetMethod(nameof(IUnsupportedInterface.Write))!, methods.Value);
        Assert.Equal(2, methods.Value.Count);
    }

    private interface ISupportedInterface
    {
        Task<int> Read(CancellationToken cancellationToken);
        ValueTask<int> Write(int value, CancellationToken cancellationToken);
    }

    private interface IUnsupportedInterface
    {
        Task<UnconfiguredValue> Read();
        ValueTask Write(UnconfiguredValue value);
    }

    private sealed class UnconfiguredValue
    {
        public int Value { get; set; }
    }
}
