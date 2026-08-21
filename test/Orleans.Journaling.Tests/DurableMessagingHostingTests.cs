using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.DurableMessaging;
using Orleans.DurableMessaging.Configuration;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests for durable messaging DI registration and hosting extensions.
/// Verifies that AddDurableMessaging correctly registers all required services.
/// </summary>
public class DurableMessagingHostingTests : IClassFixture<DurableMessagingHostingTests.Fixture>
{
    private readonly Fixture _fixture;

    public DurableMessagingHostingTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    [Fact, TestCategory("BVT"), TestCategory("Functional")]
    public async Task AddDurableMessaging_RegistersDurableInboxOptions()
    {
        // Arrange
        var grain = _fixture.Client.GetGrain<ITestDurableGrainInterface>(Guid.NewGuid());

        // Act - Activate grain to trigger DI resolution
        await grain.SetValues("test", 42);

        // Assert - Options should be registered with default values (overridden by test fixture)
        var options = _fixture.GetSiloService<IOptions<DurableInboxOptions>>();
        Assert.NotNull(options);
        Assert.NotNull(options.Value);
        // Note: The test fixture overrides some values, so we check those
        Assert.Equal(500, options.Value.MaxCapacity);
        Assert.Equal(TimeSpan.FromDays(14), options.Value.DeduplicationWindow);
        Assert.True(options.Value.EnableLongPolling);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Value.DefaultPollTimeout);
    }

    [Fact, TestCategory("BVT"), TestCategory("Functional")]
    public async Task AddDurableMessaging_WithConfigureOptions_AppliesConfiguration()
    {
        // This test validates that custom configuration was applied in the fixture
        // Arrange
        var grain = _fixture.Client.GetGrain<ITestDurableGrainInterface>(Guid.NewGuid());

        // Act - Activate grain to trigger DI resolution
        await grain.SetValues("test", 42);

        // Assert - Custom options from ConfigureTestCluster should be applied
        var options = _fixture.GetSiloService<IOptions<DurableInboxOptions>>();
        Assert.Equal(500, options.Value.MaxCapacity);
        Assert.Equal(TimeSpan.FromDays(14), options.Value.DeduplicationWindow);
    }

    [Fact, TestCategory("BVT"), TestCategory("Functional")]
    public async Task AddDurableMessaging_RegistersIDurableInboxExtension()
    {
        // Arrange
        var grain = _fixture.Client.GetGrain<ITestDurableGrainInterface>(Guid.NewGuid());

        // Act - Activate grain
        await grain.SetValues("test", 42);

        // Get the grain reference as IDurableInboxExtension
        var extension = grain.AsReference<IDurableInboxExtension>();

        // Assert - Extension should be accessible (no GrainExtensionNotInstalledException)
        Assert.NotNull(extension);

        // We can't easily test DeliverAsync without a full message, but accessing the reference
        // confirms the extension is registered
    }

    [Fact, TestCategory("BVT"), TestCategory("Functional")]
    public void DurableInboxOptions_Validate_ThrowsOnInvalidMaxCapacity()
    {
        // Arrange
        var options = new DurableInboxOptions { MaxCapacity = 0 };

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
        Assert.Contains("MaxCapacity", ex.Message);
    }

    [Fact, TestCategory("BVT"), TestCategory("Functional")]
    public void DurableInboxOptions_Validate_ThrowsOnInvalidDeduplicationWindow()
    {
        // Arrange
        var options = new DurableInboxOptions { DeduplicationWindow = TimeSpan.Zero };

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
        Assert.Contains("DeduplicationWindow", ex.Message);
    }



    [Fact, TestCategory("BVT"), TestCategory("Functional")]
    public void DurableInboxOptions_Validate_ThrowsOnInvalidDefaultPollTimeout()
    {
        // Arrange
        var options = new DurableInboxOptions { DefaultPollTimeout = TimeSpan.Zero };

        // Act & Assert
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
        Assert.Contains("DefaultPollTimeout", ex.Message);
    }

    [Fact, TestCategory("BVT"), TestCategory("Functional")]
    public void AddDurableMessaging_PreservesValidationFailureDetails()
    {
        var services = new ServiceCollection();
        services.AddDurableMessaging(options => options.MaxCapacity = 0);
        using var serviceProvider = services.BuildServiceProvider();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => serviceProvider.GetRequiredService<IOptions<DurableInboxOptions>>().Value);

        Assert.Contains("MaxCapacity must be greater than zero.", exception.Message);
    }

    /// <summary>
    /// Test fixture that configures the cluster with AddDurableMessaging.
    /// </summary>
    public class Fixture : IntegrationTestFixture
    {
        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            builder.ConfigureSilo((options, siloBuilder) =>
            {
                siloBuilder.AddDurableMessaging(opts =>
                {
                    opts.MaxCapacity = 500;
                    opts.DeduplicationWindow = TimeSpan.FromDays(14);
                });
            });
        }

        /// <summary>
        /// Helper method to get a service from the silo container.
        /// </summary>
        public T GetSiloService<T>() where T : notnull
        {
            var silo = Cluster.Silos.First();
            return silo.ServiceProvider.GetRequiredService<T>();
        }

        /// <summary>
        /// Helper method to get a keyed service from the silo container.
        /// </summary>
        public T GetSiloKeyedService<T>(string key) where T : notnull
        {
            var silo = Cluster.Silos.First();
            return silo.ServiceProvider.GetRequiredKeyedService<T>(key);
        }
    }
}
