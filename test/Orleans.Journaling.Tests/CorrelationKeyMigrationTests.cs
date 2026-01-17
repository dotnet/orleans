using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling.Messaging;
using Orleans.Serialization.Session;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests backward compatibility between deprecated CorrelationKey and HierarchicalKey.
/// Verifies that code using CorrelationKey continues to work with implicit conversions
/// to/from HierarchicalKey, and that serialization works correctly with both types.
/// </summary>
[TestCategory("BVT"), TestCategory("Functional"), TestCategory("Journaling")]
public class CorrelationKeyMigrationTests : IClassFixture<CorrelationKeyMigrationTests.Fixture>
{
    private readonly Fixture _fixture;

    public CorrelationKeyMigrationTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Tests implicit conversion from CorrelationKey to HierarchicalKey.
    /// Verifies that CorrelationKey can be transparently used where HierarchicalKey is expected.
    /// </summary>
    [Fact]
    public void ImplicitConversion_CorrelationKeyToHierarchicalKey()
    {
        // Arrange
        var correlationKey = CorrelationKey.Create("migration-test-123");

        // Act - Implicit conversion to HierarchicalKey
        HierarchicalKey? hierarchicalKey = correlationKey;

        // Assert
        Assert.NotNull(hierarchicalKey);
        Assert.Equal("migration-test-123", hierarchicalKey.ToString());
    }

    /// <summary>
    /// Tests implicit conversion from HierarchicalKey to CorrelationKey.
    /// Verifies that HierarchicalKey can be used in legacy code expecting CorrelationKey.
    /// </summary>
    [Fact]
    public void ImplicitConversion_HierarchicalKeyToCorrelationKey()
    {
        // Arrange
        var hierarchicalKey = HierarchicalKey.Create("migration-test-456");

        // Act - Implicit conversion to CorrelationKey
        CorrelationKey? correlationKey = hierarchicalKey;

        // Assert
        Assert.NotNull(correlationKey);
        Assert.Equal("migration-test-456", correlationKey.ToString());
    }

    /// <summary>
    /// Tests null handling in implicit conversions.
    /// </summary>
    [Fact]
    public void ImplicitConversion_NullHandling()
    {
        // Arrange
        CorrelationKey? nullCorrelationKey = null;
        HierarchicalKey? nullHierarchicalKey = null;

        // Act - Convert null CorrelationKey to HierarchicalKey
        HierarchicalKey? resultHierarchical = nullCorrelationKey;

        // Assert
        Assert.Null(resultHierarchical);

        // Act - Convert null HierarchicalKey to CorrelationKey
        CorrelationKey? resultCorrelation = nullHierarchicalKey;

        // Assert
        Assert.Null(resultCorrelation);
    }

    /// <summary>
    /// Tests that CorrelationKey and HierarchicalKey can be used interchangeably
    /// in operations like parent/child relationships and equality checks.
    /// </summary>
    [Fact]
    public void Interoperability_ParentChildRelationships()
    {
        // Arrange
        var correlationParent = CorrelationKey.Create("parent");
        var hierarchicalParent = HierarchicalKey.Create("parent");

        // Act - Create children using both types
        var correlationChild = correlationParent.CreateChildKey("child1");
        HierarchicalKey? hierarchicalChild = correlationChild; // Implicit conversion

        // Assert - Verify parent-child relationship works across types
        Assert.NotNull(hierarchicalChild);
        Assert.True(hierarchicalChild.IsChildOf(hierarchicalParent));

        // Act - Create child from HierarchicalKey and convert to CorrelationKey
        var hierarchicalChild2 = hierarchicalParent.CreateChildKey("child2");
        CorrelationKey? correlationChild2 = hierarchicalChild2; // Implicit conversion

        // Assert - Verify the correlation key maintains hierarchy
        Assert.NotNull(correlationChild2);
        Assert.True(correlationChild2.IsChildOf(correlationParent));
    }

    /// <summary>
    /// Tests that CorrelationKey equality works with HierarchicalKey through conversions.
    /// </summary>
    [Fact]
    public void Interoperability_Equality()
    {
        // Arrange
        var correlationKey = CorrelationKey.Create("equal-test");
        var hierarchicalKey = HierarchicalKey.Create("equal-test");

        // Act - Convert and compare
        HierarchicalKey? convertedHierarchical = correlationKey;
        CorrelationKey? convertedCorrelation = hierarchicalKey;

        // Assert - Keys should be equal after conversion
        Assert.NotNull(convertedHierarchical);
        Assert.NotNull(convertedCorrelation);
        Assert.Equal(hierarchicalKey, convertedHierarchical);
        Assert.Equal(correlationKey, convertedCorrelation);
        Assert.Equal(convertedCorrelation.ToString(), correlationKey.ToString());
        Assert.Equal(convertedHierarchical.ToString(), hierarchicalKey.ToString());
    }

    /// <summary>
    /// Tests that DurableEnvelope serialization works correctly with CorrelationKey
    /// even though the property type is HierarchicalKey.
    /// </summary>
    [Fact]
    public void DurableEnvelope_SerializationWithCorrelationKey()
    {
        // Arrange
        var sessionPool = _fixture.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var correlationKey = CorrelationKey.Create("serialization-test");
        var senderGrain = _fixture.Client.GetGrain<ITestSenderGrain>(Guid.NewGuid());
        var receiverGrain = _fixture.Client.GetGrain<ITestSenderGrain>(Guid.NewGuid());

        // Act - Create envelope with CorrelationKey (implicit conversion to HierarchicalKey)
        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = senderGrain.GetGrainId()
        };

        var envelope = builder
            .To(receiverGrain.GetGrainId(), "test-route")
            .WithCorrelationKey(correlationKey!) // Implicit conversion: CorrelationKey -> HierarchicalKey
            .WithBody(new TestMessage { Content = "correlation test" })
            .Build();

        // Assert - Verify CorrelationKey was stored as HierarchicalKey
        Assert.NotNull(envelope.CorrelationKey);
        Assert.Equal("serialization-test", envelope.CorrelationKey.ToString());
        Assert.IsType<HierarchicalKey>(envelope.CorrelationKey);

        // Act - Convert back to CorrelationKey
        CorrelationKey? roundTripKey = envelope.CorrelationKey;

        // Assert - Verify round-trip works
        Assert.NotNull(roundTripKey);
        Assert.Equal("serialization-test", roundTripKey.ToString());
    }

    /// <summary>
    /// Tests that DurableEnvelope with HierarchicalKey can be consumed by code expecting CorrelationKey.
    /// </summary>
    [Fact]
    public void DurableEnvelope_SerializationWithHierarchicalKey()
    {
        // Arrange
        var sessionPool = _fixture.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var hierarchicalKey = HierarchicalKey.Create("hierarchy-test");
        var senderGrain = _fixture.Client.GetGrain<ITestSenderGrain>(Guid.NewGuid());
        var receiverGrain = _fixture.Client.GetGrain<ITestSenderGrain>(Guid.NewGuid());

        // Act - Create envelope with HierarchicalKey
        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = senderGrain.GetGrainId()
        };

        var envelope = builder
            .To(receiverGrain.GetGrainId(), "test-route")
            .WithCorrelationKey(hierarchicalKey)
            .WithBody(new TestMessage { Content = "hierarchy test" })
            .Build();

        // Assert - Verify HierarchicalKey is stored
        Assert.NotNull(envelope.CorrelationKey);
        Assert.Equal("hierarchy-test", envelope.CorrelationKey.ToString());

        // Act - Convert to CorrelationKey for legacy code
        CorrelationKey? legacyKey = envelope.CorrelationKey;

        // Assert - Legacy code can use CorrelationKey
        Assert.NotNull(legacyKey);
        Assert.Equal("hierarchy-test", legacyKey.ToString());
    }

    /// <summary>
    /// Tests that hierarchical operations (parent/child) work across CorrelationKey and HierarchicalKey.
    /// </summary>
    [Fact]
    public void DurableEnvelope_HierarchicalCorrelationWithMixedTypes()
    {
        // Arrange
        var sessionPool = _fixture.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var parentCorrelationKey = CorrelationKey.Create("mixed-parent");
        var childHierarchicalKey = parentCorrelationKey.CreateChildKey("child");

        var senderGrain = _fixture.Client.GetGrain<ITestSenderGrain>(Guid.NewGuid());
        var receiverGrain = _fixture.Client.GetGrain<ITestSenderGrain>(Guid.NewGuid());

        // Act - Create parent envelope with CorrelationKey
        var parentBuilder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = senderGrain.GetGrainId()
        };

        var parentEnvelope = parentBuilder
            .To(receiverGrain.GetGrainId(), "parent-route")
            .WithCorrelationKey(parentCorrelationKey!) // CorrelationKey
            .WithBody(new TestMessage { Content = "parent" })
            .Build();

        // Act - Create child envelope with HierarchicalKey derived from CorrelationKey
        var childBuilder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = senderGrain.GetGrainId()
        };

        HierarchicalKey? childKey = childHierarchicalKey; // Implicit conversion
        var childEnvelope = childBuilder
            .To(receiverGrain.GetGrainId(), "child-route")
            .WithCorrelationKey(childKey!) // HierarchicalKey
            .WithBody(new TestMessage { Content = "child" })
            .Build();

        // Assert - Verify parent-child relationship preserved
        Assert.NotNull(parentEnvelope.CorrelationKey);
        Assert.NotNull(childEnvelope.CorrelationKey);
        Assert.True(childEnvelope.CorrelationKey.IsChildOf(parentEnvelope.CorrelationKey));

        // Act - Convert child back to CorrelationKey and verify
        CorrelationKey? childAsCorrelation = childEnvelope.CorrelationKey;
        Assert.NotNull(childAsCorrelation);
        Assert.True(childAsCorrelation.IsChildOf(parentCorrelationKey));
    }

    /// <summary>
    /// Test fixture that configures the cluster with serialization support.
    /// </summary>
    public class Fixture : IntegrationTestFixture
    {
        public IServiceProvider ServiceProvider => Client.ServiceProvider;

        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            builder.ConfigureSilo((options, siloBuilder) =>
            {
                siloBuilder.AddDurableMessaging(opts =>
                {
                    opts.MaxCapacity = 100;
                    opts.DeduplicationWindow = TimeSpan.FromDays(7);
                });
            });
        }
    }
}
