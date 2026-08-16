using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Streams;
using TestExtensions;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
[TestCategory("Streaming")]
public sealed class ImplicitStreamPubSubTests
{
    [Fact]
    public void CreateSubscriptionId_ForExplicitSubscription_ProvidesConfigurationGuidance()
    {
        const string providerName = "ImplicitOnlyProvider";
        var manifestProvider = Substitute.For<IClusterManifestProvider>();
        manifestProvider.Current.Returns(
            new ClusterManifest(
                MajorMinorVersion.Zero,
                ImmutableDictionary<SiloAddress, GrainManifest>.Empty));

        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var subscriberTable = new ImplicitStreamSubscriberTable(
            new GrainBindingsResolver(manifestProvider),
            [],
            serviceProvider);
        var pubSub = new ImplicitStreamPubSub(subscriberTable);
        var streamId = new QualifiedStreamId(providerName, StreamId.Create("namespace", Guid.NewGuid()));

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => pubSub.CreateSubscriptionId(streamId, default));

        Assert.Contains($"stream provider '{providerName}'", exception.Message);
        Assert.Contains($"{nameof(StreamPubSubType)}.{nameof(StreamPubSubType.ImplicitOnly)}", exception.Message);
        Assert.Contains($"{nameof(StreamPubSubType)}.{nameof(StreamPubSubType.ExplicitGrainBasedAndImplicit)}", exception.Message);
        Assert.Contains($"{nameof(StreamPubSubType)}.{nameof(StreamPubSubType.ExplicitGrainBasedOnly)}", exception.Message);
        Assert.Contains("ConfigureStreamPubSub", exception.Message);
    }
}
