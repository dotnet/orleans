using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Messaging.Configuration;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Messaging.Tests.Hosting;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class PublicMessagingRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddMessaging_RepeatedPublicRegistrationRetainsOneConfiguratorAndScopedBindings(bool useSiloBuilder)
    {
        var builder = new TestSiloBuilder();
        builder.AddJournaling();
        var services = builder.Services;
        for (var invocation = 0; invocation < 2; invocation++)
        {
            if (useSiloBuilder)
            {
                Assert.Same(builder, builder.AddMessaging());
            }
            else
            {
                Assert.Same(services, services.AddMessaging());
            }
        }

        var configurator = Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(IConfigureGrainTypeComponents)
            && descriptor.ImplementationType == typeof(MessagingGrainTypeConfigurator));
        Assert.Equal(ServiceLifetime.Singleton, configurator.Lifetime);
        foreach (var contract in new[]
        {
            typeof(MessagingPump), typeof(Inbox), typeof(IInbox), typeof(IMessagingDiagnostics)
        })
        {
            var descriptor = Assert.Single(services, descriptor => descriptor.ServiceType == contract && !descriptor.IsKeyedService);
            Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        }

        // The configurator binds the messaging extension at activation; the only keyed grain extension is the durable-jobs receiver.
        var extension = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IGrainExtension));
        Assert.Equal(typeof(Orleans.DurableJobs.DurableJob).Assembly, Assert.IsAssignableFrom<Type>(extension.ServiceKey).Assembly);
    }

    [Fact]
    public void AddMessaging_PreservesCallerSuppliedTimeProviderAndAppliesOptions()
    {
        var expectedTime = new FixedTimeProvider(new DateTimeOffset(2040, 2, 3, 4, 5, 6, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(expectedTime);

        services.AddMessaging(options =>
        {
            options.MaxCapacity = 17;
            options.InboxBatchSize = 4;
            options.OutboxBatchSize = 5;
        });
        using var provider = services.BuildServiceProvider();

        Assert.Same(expectedTime, provider.GetRequiredService<TimeProvider>());
        var configured = provider.GetRequiredService<IOptions<DurableMessagingOptions>>().Value;
        Assert.Equal(17, configured.MaxCapacity);
        Assert.Equal(4, configured.InboxBatchSize);
        Assert.Equal(5, configured.OutboxBatchSize);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("orleans-binary")]
    public void AddMessaging_PreservesConfiguredJournalFormat(string format)
    {
        var services = new ServiceCollection();
        services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = format);
        services.AddMessaging();
        using var provider = services.BuildServiceProvider();

        Assert.Equal(format, provider.GetRequiredService<IOptions<JournaledStateManagerOptions>>().Value.JournalFormatKey);
    }

    [Fact]
    public void AddMessaging_InvalidOptionsFailThroughOptionsContract()
    {
        var services = new ServiceCollection();
        services.AddMessaging(options => options.MaxCapacity = 0);
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<DurableMessagingOptions>>().Value);

        Assert.Contains("DurableMessagingOptions validation failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddMessaging_NullDeliveryResiliencePipelineFailsThroughOptionsContract()
    {
        var services = new ServiceCollection();
        services.AddMessaging(options => options.DeliveryResiliencePipeline = null!);
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<DurableMessagingOptions>>().Value);

        Assert.Contains("DurableMessagingOptions validation failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddMessaging_BindsInboxContractToDeliveredInboxOverEarlierRegistration()
    {
        var services = new ServiceCollection();
        services.AddScoped<IInbox, TestInbox>();
        services.AddMessaging();

        // Inbox construction needs an activation, so substitute an uninitialized scoped instance to observe the binding.
        services.Replace(ServiceDescriptor.Scoped(static _ => (Inbox)RuntimeHelpers.GetUninitializedObject(typeof(Inbox))));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IInbox));
        Assert.Same(scope.ServiceProvider.GetRequiredService<Inbox>(), scope.ServiceProvider.GetRequiredService<IInbox>());
    }

    [Fact]
    public async Task AddMessaging_DoesNotReplaceUnrelatedConstructionErrors()
    {
        var services = new ServiceCollection();
        services.AddMessaging();
        services.AddScoped<IJournaledStateManager, ConstructionTestStateManager>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var exception = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<Inbox>());

        Assert.Contains(nameof(IGrainContext), exception.Message, StringComparison.Ordinal);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class ConstructionTestStateManager : IJournaledStateManager
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;
        public void RegisterStateMachine(string name, IStateMachine state) { }
        public bool TryGetStateMachine(string name, [NotNullWhen(true)] out IStateMachine? state)
        {
            state = null;
            return false;
        }

        public ValueTask WriteStateAsync(CancellationToken cancellationToken) => default;
        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => default;
    }
}
