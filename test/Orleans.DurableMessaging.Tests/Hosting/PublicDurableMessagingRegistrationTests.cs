using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.DurableMessaging.Configuration;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Hosting;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class PublicDurableMessagingRegistrationTests
{
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
            () => validate.Invoke(null, [context]));

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
            () => validate.Invoke(null, [context]));

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
            () => validate.Invoke(null, [context]));

        var diagnostic = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("one activation", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("stateless worker", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddDurableMessaging_InvalidOptionsPreserveValidationDetailsAtStartup()
    {
        var services = new ServiceCollection();
        services.AddDurableMessaging(options => options.MaxCapacity = 0);
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Equal(typeof(DurableInboxOptions), exception.OptionsType);
        Assert.Contains(nameof(DurableInboxOptions.MaxCapacity), exception.Message, StringComparison.Ordinal);
        Assert.Contains("must be greater than zero", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Actual value was 0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddDurableMessaging_DoesNotReplaceUnrelatedConstructionErrors()
    {
        var services = new ServiceCollection();
        services.AddDurableMessaging();
        services.AddScoped<IJournaledStateManager, FullyCapableStateManager>();
        await using var provider = services.BuildServiceProvider();
        var extensionType = services.Single(descriptor => descriptor.ServiceType.Name == "DurableInboxExtension").ServiceType;

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService(extensionType));

        Assert.DoesNotContain("observer support", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InboxTransportIsInternal()
    {
        Assert.False(typeof(IDurableInboxExtension).IsPublic);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
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

    private class RollbackOnlyStateManager : IJournaledStateManager
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;
        public void RegisterState(string name, IJournaledState state) { }
        public virtual void RegisterObserver(IJournaledStateObserver observer) =>
            throw new NotSupportedException();
        public bool TryGetState(string name, [NotNullWhen(true)] out IJournaledState? state)
        {
            state = null;
            return false;
        }

        public ValueTask WriteStateAsync(CancellationToken cancellationToken) => default;
        public ValueTask RevertPendingChangesAsync(CancellationToken cancellationToken) => default;
        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class FullyCapableStateManager : RollbackOnlyStateManager
    {
        public override void RegisterObserver(IJournaledStateObserver observer) { }
    }
}
