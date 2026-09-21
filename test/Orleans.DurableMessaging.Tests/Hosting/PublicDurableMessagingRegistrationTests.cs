using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.DurableMessaging.Configuration;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Runtime;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Hosting;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class PublicDurableMessagingRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddDurableMessaging_RepeatedPublicRegistrationRetainsOneConfiguratorAndScopedBindings(bool useSiloBuilder)
    {
        var builder = new TestSiloBuilder();
        builder.AddJournaling();
        var services = builder.Services;
        for (var invocation = 0; invocation < 2; invocation++)
        {
            if (useSiloBuilder)
            {
                Assert.Same(builder, builder.AddDurableMessaging());
            }
            else
            {
                Assert.Same(services, services.AddDurableMessaging());
            }
        }

        var configuratorType = typeof(IDurableInbox).Assembly.GetType(
            "Orleans.DurableMessaging.DurableMessagingGrainTypeConfigurator", throwOnError: true)!;
        var configurator = Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(IConfigureGrainTypeComponents) && descriptor.ImplementationType == configuratorType);
        Assert.Equal(ServiceLifetime.Singleton, configurator.Lifetime);
        foreach (var contract in new[] { typeof(IDurableInbox), typeof(IDurableOutbox), typeof(IDurableMessagingDiagnostics) })
        {
            var descriptor = Assert.Single(services, descriptor => descriptor.ServiceType == contract && !descriptor.IsKeyedService);
            Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        }

        foreach (var stateName in new[]
        {
            "inbox", "inbox-processed", "inbox-message-state", "inbox-dead-letters",
            "outbox", "outbox-message-state", "outbox-dead-letters", "outbox-job-id",
            "outbox-job-handle", "outbox-completed-job-id"
        })
        {
            Assert.DoesNotContain(services, descriptor =>
                descriptor.IsKeyedService && Equals(descriptor.ServiceKey, $"__orleans.durable-messaging.{stateName}")
                && descriptor.ServiceType.IsGenericType
                && (descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IDurableDictionary<,>)
                    || descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IDurableValue<>)));
        }
        foreach (var stateType in new[] { typeof(IDurableDictionary<,>), typeof(IDurableValue<>) })
        {
            Assert.Single(services, descriptor => descriptor.IsKeyedService
                && Equals(descriptor.ServiceKey, KeyedService.AnyKey) && descriptor.ServiceType == stateType);
        }
        var sequence = Assert.Single(services, descriptor => descriptor.IsKeyedService
            && Equals(descriptor.ServiceKey, "__orleans.durable-messaging.outbox-job-sequence")
            && descriptor.ServiceType == typeof(IDurableValue<long>));
        Assert.Equal(ServiceLifetime.Scoped, sequence.Lifetime);
        var extension = Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(IGrainExtension) && Equals(descriptor.ServiceKey, typeof(IDurableInboxExtension)));
        Assert.Equal(ServiceLifetime.Scoped, extension.Lifetime);
    }

    [Fact]
    public void AddDurableMessaging_RegistersPublicScopedContractsAndInboxExtensionKey()
    {
        var services = new ServiceCollection();

        services.AddDurableMessaging();

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IDurableInbox));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IDurableOutbox));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IDurableMessagingDiagnostics));
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IGrainExtension)
                && Equals(descriptor.ServiceKey, typeof(IDurableInboxExtension)));
    }

    [Fact]
    public void AddDurableMessaging_PreservesCallerSuppliedTimeProviderAndAppliesOptions()
    {
        var expectedTime = new FixedTimeProvider(new DateTimeOffset(2040, 2, 3, 4, 5, 6, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(expectedTime);

        services.AddDurableMessaging(options =>
        {
            options.MaxCapacity = 17;
            options.InboxBatchSize = 4;
            options.OutboxBatchSize = 5;
        });
        using var provider = services.BuildServiceProvider();

        Assert.Same(expectedTime, provider.GetRequiredService<TimeProvider>());
        var configured = provider.GetRequiredService<IOptions<DurableInboxOptions>>().Value;
        Assert.Equal(17, configured.MaxCapacity);
        Assert.Equal(4, configured.InboxBatchSize);
        Assert.Equal(5, configured.OutboxBatchSize);
    }

    [Fact]
    public void AddDurableMessaging_SelectsBinaryJournalFormat()
    {
        var services = new ServiceCollection();
        services.AddOptions<JournaledStateManagerOptions>();
        services.AddDurableMessaging();
        using var provider = services.BuildServiceProvider();

        Assert.Equal(
            "orleans-binary",
            provider.GetRequiredService<IOptions<JournaledStateManagerOptions>>().Value.JournalFormatKey);
    }

    [Fact]
    public void AddDurableMessaging_PreservesExplicitBinaryJournalFormat()
    {
        var services = new ServiceCollection();
        services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = "orleans-binary");
        services.AddDurableMessaging();
        using var provider = services.BuildServiceProvider();

        Assert.Equal(
            "orleans-binary",
            provider.GetRequiredService<IOptions<JournaledStateManagerOptions>>().Value.JournalFormatKey);
    }

    [Fact]
    public void AddDurableMessaging_RejectsConflictingJournalFormat()
    {
        var services = new ServiceCollection();
        services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = "custom-format");
        services.AddDurableMessaging();
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IOptions<JournaledStateManagerOptions>>().Value);

        Assert.Contains("custom-format", exception.Message, StringComparison.Ordinal);
        Assert.Contains("orleans-binary", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivationValidator_RejectsReentrantGrainTypes()
    {
        var validatorType = typeof(IDurableInbox).Assembly.GetType(
            "Orleans.DurableMessaging.DurableMessagingActivationValidator",
            throwOnError: true)!;
        var validate = validatorType.GetMethod(
            "Validate",
            BindingFlags.Static | BindingFlags.Public)!;
        var context = Substitute.For<IGrainContext>();
        context.GrainInstance.Returns(new ReentrantTestGrain());

        var exception = Assert.Throws<TargetInvocationException>(
            () => validate.Invoke(null, [context, GetGrainProperties(typeof(ReentrantTestGrain)), new RandomPlacement()]));

        var diagnostic = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("non-reentrant", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(typeof(ReentrantTestGrain).ToString(), diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivationValidator_RejectsAlwaysInterleaveMethods()
    {
        var validatorType = typeof(IDurableInbox).Assembly.GetType(
            "Orleans.DurableMessaging.DurableMessagingActivationValidator",
            throwOnError: true)!;
        var validate = validatorType.GetMethod(
            "Validate",
            BindingFlags.Static | BindingFlags.Public)!;
        var context = Substitute.For<IGrainContext>();
        context.GrainInstance.Returns(new InterleavableTestGrain());

        var exception = Assert.Throws<TargetInvocationException>(
            () => validate.Invoke(null, [context, GetGrainProperties(typeof(InterleavableTestGrain)), new RandomPlacement()]));

        var diagnostic = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("interleavable method", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(IInterleavableBase.PingAsync), diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivationValidator_RejectsStatelessWorkers()
    {
        var validatorType = typeof(IDurableInbox).Assembly.GetType(
            "Orleans.DurableMessaging.DurableMessagingActivationValidator",
            throwOnError: true)!;
        var validate = validatorType.GetMethod(
            "Validate",
            BindingFlags.Static | BindingFlags.Public)!;
        var context = Substitute.For<IGrainContext>();
        context.GrainInstance.Returns(new StatelessWorkerTestGrain());

        var exception = Assert.Throws<TargetInvocationException>(
            () => validate.Invoke(null, [context, GetGrainProperties(typeof(StatelessWorkerTestGrain)), new Orleans.Concurrency.StatelessWorkerAttribute().PlacementStrategy]));

        var diagnostic = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("one activation", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("stateless worker", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddDurableMessaging_InvalidOptionsFailThroughOptionsContract()
    {
        var services = new ServiceCollection();
        services.AddDurableMessaging(options => options.MaxCapacity = 0);
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<DurableInboxOptions>>().Value);

        Assert.Contains("DurableInboxOptions validation failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddDurableMessaging_DoesNotReplaceUnrelatedConstructionErrors()
    {
        var services = new ServiceCollection();
        services.AddDurableMessaging();
        services.AddScoped<IJournaledStateManager, ConstructionTestStateManager>();
        await using var provider = services.BuildServiceProvider();
        var extensionType = services.Single(descriptor => descriptor.ServiceType.Name == "DurableInboxExtension").ServiceType;

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService(extensionType));

        Assert.Contains(nameof(IGrainContext), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalConsumerAssembly_HasNoFriendAccessToDurableMessaging()
    {
        var sourceAssembly = typeof(IDurableInbox).Assembly;
        var consumerName = typeof(PublicDurableMessagingRegistrationTests).Assembly.GetName().Name;
        var friendDeclarations = sourceAssembly
            .GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute")
            .Select(attribute => attribute.ConstructorArguments[0].Value?.ToString())
            .ToArray();

        Assert.DoesNotContain(friendDeclarations, declaration =>
            declaration?.StartsWith(consumerName!, StringComparison.Ordinal) == true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static GrainProperties GetGrainProperties(Type grainType)
    {
        var values = new Dictionary<string, string>();
        new AttributeGrainPropertiesProvider(Substitute.For<IServiceProvider>())
            .Populate(grainType, GrainType.Create("public-registration"), values);
        return new GrainProperties(values.ToImmutableDictionary(StringComparer.Ordinal));
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    [Orleans.Concurrency.Reentrant]
    private sealed class ReentrantTestGrain
    {
    }

    public interface IInterleavableBase
    {
        [Orleans.Concurrency.AlwaysInterleave]
        Task PingAsync();
    }

    public interface IInterleavableTestGrain : IGrain, IInterleavableBase
    {
    }

    private sealed class InterleavableTestGrain : IInterleavableTestGrain
    {
        public Task PingAsync() => Task.CompletedTask;
    }

    [Orleans.Concurrency.StatelessWorker]
    private sealed class StatelessWorkerTestGrain
    {
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
