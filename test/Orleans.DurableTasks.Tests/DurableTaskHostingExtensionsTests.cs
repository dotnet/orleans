#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.DurableMessaging;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Runtime.DurableTasks;
using Orleans.Serialization;
using Orleans.Serialization.Configuration;
using Xunit;

namespace Orleans.DurableTasks.Tests;

/// <summary>
/// Tests for <see cref="DurableTaskHostingExtensions"/> (namespace <c>Orleans.Hosting</c>), covering both
/// the <see cref="IClientBuilder"/> and <see cref="ISiloBuilder"/> DI wiring overloads. These tests assert on the
/// actual <see cref="IServiceCollection"/> registrations (service type, implementation type, lifetime) produced by
/// the extension methods and on same-instance-per-scope behavior for forwarded services, following the pattern in
/// <c>test/Orleans.Journaling.Tests/DurableMessagingHostingTests.cs</c> / <c>KeyedJournalingRegistrationTests.cs</c>.
/// </summary>
[TestCategory("BVT")]
public class DurableTaskHostingExtensionsTests
{
    private static string DurableTaskResponseAssemblyName =>
        typeof(System.Distributed.DurableTasks.DurableTaskResponse).Assembly.GetName().Name!;

    // ----------------------------------------------------------------------------------------
    // IClientBuilder overload
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void AddDurableTasks_ClientBuilder_ReturnsSameBuilderInstance()
    {
        var builder = new TestClientBuilder();

        var result = builder.AddDurableTasks();

        Assert.Same(builder, result);
    }

    [Fact]
    public void AddDurableTasks_ClientBuilder_ConfiguresTypeManifestOptionsWithDurableTaskResponseAssembly()
    {
        var builder = new TestClientBuilder();

        builder.AddDurableTasks();

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<TypeManifestOptions>>().Value;

        Assert.Contains(DurableTaskResponseAssemblyName, options.AllowedAssemblies);
    }

    [Fact]
    public void AddDurableTasks_ClientBuilder_DoesNotRegisterSiloOnlyServices()
    {
        // Regression guard: the client overload must not accidentally pull in silo-only wiring
        // (durable messaging, the grain runtime, or the RPC transport/handler/participant types).
        var builder = new TestClientBuilder();

        builder.AddDurableTasks();

        Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(DurableTaskGrainRuntimeShared));
        Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(DurableTaskGrainRuntime));
        Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(DurableTaskMessageTransport));
        Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(TimeProvider));
    }

    // ----------------------------------------------------------------------------------------
    // ISiloBuilder overload - registration shape (no full resolution required)
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void AddDurableTasks_SiloBuilder_ReturnsSameBuilderInstance()
    {
        var builder = new TestSiloBuilder();

        var result = builder.AddDurableTasks();

        Assert.Same(builder, result);
    }

    [Fact]
    public void AddDurableTasks_SiloBuilder_ConfiguresTypeManifestOptionsWithDurableTaskResponseAssembly()
    {
        var builder = new TestSiloBuilder();

        builder.AddDurableTasks();

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<TypeManifestOptions>>().Value;

        Assert.Contains(DurableTaskResponseAssemblyName, options.AllowedAssemblies);
    }

    [Fact]
    public void AddDurableTasks_SiloBuilder_CallsAddDurableMessaging()
    {
        // AddDurableMessaging registers DurableMessagingInstruments as a singleton; its presence is a reliable,
        // low-level signal that AddDurableMessaging() was actually invoked (as opposed to duplicating its wiring).
        var builder = new TestSiloBuilder();

        builder.AddDurableTasks();

        Assert.Contains(builder.Services, d => d.ServiceType.Name == "DurableMessagingInstruments");
    }

    [Fact]
    public void AddDurableTasks_SiloBuilder_RegistersDurableTaskGrainRuntimeSharedAsSingleton()
    {
        var builder = new TestSiloBuilder();

        builder.AddDurableTasks();

        var descriptor = Assert.Single(builder.Services, d => d.ServiceType == typeof(DurableTaskGrainRuntimeShared));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(typeof(DurableTaskGrainRuntimeShared), descriptor.ImplementationType);
    }

    [Theory]
    [InlineData(typeof(DurableTaskGrainRuntime))]
    [InlineData(typeof(DurableTaskMessageTransport))]
    [InlineData(typeof(DurableTaskMessageHandler))]
    [InlineData(typeof(DurableTaskGrainParticipant))]
    public void AddDurableTasks_SiloBuilder_RegistersCoreTypesAsScoped(Type serviceType)
    {
        var builder = new TestSiloBuilder();

        builder.AddDurableTasks();

        var descriptor = Assert.Single(builder.Services, d => d.ServiceType == serviceType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.Equal(serviceType, descriptor.ImplementationType);
    }

    [Fact]
    public void AddDurableTasks_SiloBuilder_ForwardsIDurableTaskGrainRuntimeAsScoped()
    {
        var builder = new TestSiloBuilder();

        builder.AddDurableTasks();

        var descriptor = Assert.Single(builder.Services, d => d.ServiceType == typeof(IDurableTaskGrainRuntime));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.NotNull(descriptor.ImplementationFactory);
    }

    [Fact]
    public void AddDurableTasks_SiloBuilder_ForwardsIDurableTaskMessageTransportAsScoped()
    {
        var builder = new TestSiloBuilder();

        builder.AddDurableTasks();

        var descriptor = Assert.Single(builder.Services, d => d.ServiceType == typeof(IDurableTaskMessageTransport));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.NotNull(descriptor.ImplementationFactory);
    }

    [Fact]
    public void AddDurableTasks_SiloBuilder_RegistersIInboxHandlerAsScoped()
    {
        var builder = new TestSiloBuilder();

        builder.AddDurableTasks();

        var descriptor = Assert.Single(builder.Services, d => d.ServiceType == typeof(IInboxHandler));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddDurableTasks_SiloBuilder_RegistersDurableTaskGrainParticipantAsIJournaledGrainParticipant()
    {
        var builder = new TestSiloBuilder();

        builder.AddDurableTasks();

        var descriptors = builder.Services
            .Where(d => d.ServiceType == typeof(IJournaledGrainParticipant))
            .ToList();
        Assert.Equal(2, descriptors.Count);
        Assert.All(descriptors, descriptor =>
        {
            Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
            Assert.NotNull(descriptor.ImplementationFactory);
            Assert.Null(descriptor.ImplementationType);
        });
    }

    [Fact]
    public void AddDurableTasks_SiloBuilder_RegistersKeyedGrainExtensionsForDurableTaskGrainExtensionAndServer()
    {
        var builder = new TestSiloBuilder();

        builder.AddDurableTasks();

        var extensionDescriptor = Assert.Single(
            builder.Services,
            d => d.ServiceType == typeof(IGrainExtension) && Equals(d.ServiceKey, typeof(IDurableTaskGrainExtension)));
        Assert.Equal(ServiceLifetime.Transient, extensionDescriptor.Lifetime);

        var serverDescriptor = Assert.Single(
            builder.Services,
            d => d.ServiceType == typeof(IGrainExtension) && Equals(d.ServiceKey, typeof(IDurableTaskServer)));
        Assert.Equal(ServiceLifetime.Transient, serverDescriptor.Lifetime);
    }

    [Fact]
    public void AddDurableTasks_SiloBuilder_RegistersTimeProviderSystemAsSingleton_WhenNoneRegistered()
    {
        var builder = new TestSiloBuilder();

        builder.AddDurableTasks();

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var timeProvider = serviceProvider.GetRequiredService<TimeProvider>();

        Assert.Same(TimeProvider.System, timeProvider);
    }

    [Fact]
    public void AddDurableTasks_SiloBuilder_TryAddSingletonTimeProvider_DoesNotOverridePreRegisteredTimeProvider()
    {
        var builder = new TestSiloBuilder();
        var customTimeProvider = new FakeTimeProvider();
        builder.Services.AddSingleton<TimeProvider>(customTimeProvider);

        builder.AddDurableTasks();

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var resolved = serviceProvider.GetRequiredService<TimeProvider>();

        Assert.Same(customTimeProvider, resolved);
        Assert.NotSame(TimeProvider.System, resolved);
        // Only one TimeProvider registration should exist - AddDurableMessaging()/AddDurableTasks() both use
        // TryAddSingleton, so a pre-existing registration must prevent either from adding a second one.
        Assert.Single(builder.Services, d => d.ServiceType == typeof(TimeProvider));
    }

    // ----------------------------------------------------------------------------------------
    // ISiloBuilder overload - full DI resolution (same-instance-per-scope checks)
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void AddDurableTasks_SiloBuilder_IDurableTaskGrainRuntimeResolvesToSameInstanceAsConcreteTypeWithinScope()
    {
        using var serviceProvider = CreateResolvableSiloBuilder().Services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        var concrete = scope.ServiceProvider.GetRequiredService<DurableTaskGrainRuntime>();
        var viaInterface = scope.ServiceProvider.GetRequiredService<IDurableTaskGrainRuntime>();

        Assert.Same(concrete, viaInterface);
    }

    [Fact]
    public void AddDurableTasks_SiloBuilder_IDurableTaskGrainRuntimeIsDifferentInstancePerScope()
    {
        using var serviceProvider = CreateResolvableSiloBuilder().Services.BuildServiceProvider();
        using var scopeA = serviceProvider.CreateScope();
        using var scopeB = serviceProvider.CreateScope();

        var a = scopeA.ServiceProvider.GetRequiredService<DurableTaskGrainRuntime>();
        var b = scopeB.ServiceProvider.GetRequiredService<DurableTaskGrainRuntime>();

        Assert.NotSame(a, b);
    }

    [Fact]
    public void AddDurableTasks_SiloBuilder_IDurableTaskMessageTransportResolvesToSameInstanceAsConcreteTypeWithinScope()
    {
        using var serviceProvider = CreateResolvableSiloBuilder().Services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        var concrete = scope.ServiceProvider.GetRequiredService<DurableTaskMessageTransport>();
        var viaInterface = scope.ServiceProvider.GetRequiredService<IDurableTaskMessageTransport>();

        Assert.Same(concrete, viaInterface);
    }

    [Fact]
    public void AddDurableTasks_SiloBuilder_IInboxHandlerResolvesToSameInstanceAsDurableTaskMessageHandler()
    {
        using var serviceProvider = CreateResolvableSiloBuilder().Services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        var concrete = scope.ServiceProvider.GetRequiredService<DurableTaskMessageHandler>();
        var viaInbox = scope.ServiceProvider.GetRequiredService<IInboxHandler>();

        Assert.Same(concrete, viaInbox);
    }

    [Fact]
    public void AddDurableTasks_SiloBuilder_IJournaledGrainParticipantEnumerableIncludesDurableTaskGrainParticipant()
    {
        using var serviceProvider = CreateResolvableSiloBuilder().Services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        var participants = scope.ServiceProvider.GetServices<IJournaledGrainParticipant>().ToList();
        var durableTaskParticipant = Assert.Single(participants, p => p is DurableTaskGrainParticipant);
        var concreteParticipant = scope.ServiceProvider.GetRequiredService<DurableTaskGrainParticipant>();
        Assert.Same(concreteParticipant, durableTaskParticipant);

        var participantsAgain = scope.ServiceProvider.GetServices<IJournaledGrainParticipant>().ToList();
        var durableTaskParticipantAgain = Assert.Single(participantsAgain, p => p is DurableTaskGrainParticipant);
        Assert.Same(durableTaskParticipant, durableTaskParticipantAgain);

        var durableMessagingParticipant = Assert.Single(participants, p => p is DurableMessagingGrainParticipant);
        var concreteMessagingParticipant = scope.ServiceProvider.GetRequiredService<DurableMessagingGrainParticipant>();
        Assert.Same(concreteMessagingParticipant, durableMessagingParticipant);
    }

    [Fact]
    public void AddDurableTasks_SiloBuilder_KeyedGrainExtension_DurableTaskGrainExtensionResolvesToRuntimeInstance()
    {
        using var serviceProvider = CreateResolvableSiloBuilder().Services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        var runtime = scope.ServiceProvider.GetRequiredService<DurableTaskGrainRuntime>();
        var viaKeyedExtension = scope.ServiceProvider.GetRequiredKeyedService<IGrainExtension>(typeof(IDurableTaskGrainExtension));

        var typedExtension = Assert.IsType<DurableTaskGrainRuntime>(viaKeyedExtension);
        Assert.Same(runtime, typedExtension);
    }

    [Fact]
    public void AddDurableTasks_SiloBuilder_KeyedGrainExtension_DurableTaskServerResolvesToRuntimeInstance()
    {
        using var serviceProvider = CreateResolvableSiloBuilder().Services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        var runtime = scope.ServiceProvider.GetRequiredService<DurableTaskGrainRuntime>();
        var viaKeyedServer = scope.ServiceProvider.GetRequiredKeyedService<IGrainExtension>(typeof(IDurableTaskServer));

        var typedServer = Assert.IsType<DurableTaskGrainRuntime>(viaKeyedServer);
        Assert.Same(runtime, typedServer);
    }

    /// <summary>
    /// Builds a <see cref="TestSiloBuilder"/> with the minimal set of fakes/real fakeable services required to fully
    /// resolve <see cref="DurableTaskGrainRuntime"/>, <see cref="DurableTaskMessageTransport"/>,
    /// <see cref="DurableTaskMessageHandler"/> and <see cref="DurableTaskGrainParticipant"/> end-to-end via
    /// <see cref="AddDurableTasks(ISiloBuilder)"/>, without spinning up a full silo/cluster.
    /// </summary>
    private static TestSiloBuilder CreateResolvableSiloBuilder()
    {
        var builder = new TestSiloBuilder();
        builder.Services.AddLogging();
        builder.Services.AddSerializer();
        builder.Services.AddMetrics();
        builder.Services.AddSingleton<OrleansInstruments>();
        builder.Services.AddSingleton<IGrainContext>(new TestGrainContext(GrainId.Create("test-grain", "1")));
        builder.Services.AddSingleton<IGrainContextAccessor>(sp => new TestGrainContextAccessor(sp.GetRequiredService<IGrainContext>()));
        builder.Services.AddScoped<IDurableTaskGrainStorage, VolatileDurableTaskGrainStorage>();
        builder.Services.AddScoped<IDurableOutbox, FakeDurableOutbox>();
        builder.Services.AddScoped<IDurableMessageScheduler, FakeDurableMessageScheduler>();
        builder.Services.AddScoped<IJournaledStateManager, FakeJournaledStateManager>();

        // AddDurableTasks(ISiloBuilder) also calls AddDurableMessaging(), which registers
        // DurableMessagingGrainParticipant as an additional IJournaledGrainParticipant. Resolving the full
        // IJournaledGrainParticipant enumerable (as the "includes DurableTaskGrainParticipant" test below does)
        // therefore also resolves DurableMessagingGrainParticipant -> IDurableInbox, which needs these keyed
        // dictionaries/value. None of this is exercised by DurableTaskGrainRuntime/Transport/Handler themselves -
        // it is purely a side effect of enumerating IJournaledGrainParticipant, but it must still be satisfiable.
        builder.Services.AddKeyedSingleton<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>("inbox", new FakeDurableDictionary<(GrainId, Guid), DurableEnvelope>());
        builder.Services.AddKeyedSingleton<IDurableDictionary<(GrainId, Guid), DateTimeOffset>>("inbox-processed", new FakeDurableDictionary<(GrainId, Guid), DateTimeOffset>());
        builder.Services.AddKeyedSingleton<IDurableValue<string>>("inbox-job-id", new FakeDurableValue<string>());

        // InboxMessageState/InboxDeadLetter are internal to Orleans.DurableMessaging, and Orleans.DurableTasks.Tests
        // has no InternalsVisibleTo grant from that assembly (only Orleans.Journaling.Tests does), so these two
        // keyed registrations must be built via reflection rather than naming the closed generic types directly.
        var messagingAssembly = typeof(DurableEnvelope).Assembly;
        var grainGuidKeyType = typeof(ValueTuple<GrainId, Guid>);
        var dictionaryInterfaceDefinition = typeof(IDurableDictionary<,>);
        var fakeDictionaryDefinition = typeof(FakeDurableDictionary<,>);

        foreach (var (typeName, key) in new[] { ("Orleans.DurableMessaging.InboxMessageState", "inbox-message-state"), ("Orleans.DurableMessaging.InboxDeadLetter", "inbox-dead-letters") })
        {
            var valueType = messagingAssembly.GetType(typeName) ?? throw new InvalidOperationException($"Type '{typeName}' not found in {messagingAssembly}.");
            var serviceType = dictionaryInterfaceDefinition.MakeGenericType(grainGuidKeyType, valueType);
            var implementationType = fakeDictionaryDefinition.MakeGenericType(grainGuidKeyType, valueType);
            var instance = Activator.CreateInstance(implementationType) ?? throw new InvalidOperationException($"Failed to create instance of '{implementationType}'.");
            builder.Services.AddKeyedSingleton(serviceType, key, instance);
        }

        builder.AddDurableTasks();
        return builder;
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class TestClientBuilder : IClientBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    /// <summary>
    /// No-op <see cref="IDurableOutbox"/> fake sufficient to satisfy DI construction of
    /// <see cref="DurableTaskMessageTransport"/>; no test in this file exercises its behavior.
    /// </summary>
    private sealed class FakeDurableOutbox : IDurableOutbox
    {
        public int Count => 0;
        public IEnumerable<DurableEnvelope> Messages => [];
        public void Send(DurableEnvelope envelope) => throw new NotImplementedException();
        public bool RemoveMessage(Guid messageId) => throw new NotImplementedException();
        public bool TryGetMessage(Guid messageId, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out DurableEnvelope envelope) => throw new NotImplementedException();
        public Task DeliverPendingMessagesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    /// <summary>
    /// No-op <see cref="IDurableMessageScheduler"/> fake sufficient to satisfy DI construction of
    /// <see cref="DurableTaskMessageTransport"/>; no test in this file exercises its behavior.
    /// </summary>
    private sealed class FakeDurableMessageScheduler : IDurableMessageScheduler
    {
        public ValueTask ScheduleAsync(DurableEnvelope message, DateTimeOffset dueTime, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    /// <summary>
    /// No-op <see cref="IJournaledStateManager"/> fake sufficient to satisfy DI construction of
    /// <see cref="DurableTaskMessageTransport"/>; no test in this file exercises its behavior.
    /// </summary>
    private sealed class FakeJournaledStateManager : IJournaledStateManager, IDisposable
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public void RegisterState(string name, IJournaledState state) => throw new NotImplementedException();
        public bool TryGetState(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IJournaledState? state) => throw new NotImplementedException();
        public ValueTask WriteStateAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask RevertPendingChangesAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

        // The DI container's synchronous scope disposal requires IDisposable in addition to the interface's
        // default IAsyncDisposable.DisposeAsync() implementation; without this, disposing a `using var scope`
        // that resolved this fake throws InvalidOperationException at scope teardown.
        public void Dispose() { }
    }

    /// <summary>
    /// Minimal in-memory <see cref="IDurableDictionary{K, V}"/> fake sufficient to satisfy DI construction of
    /// <c>DurableInbox</c>/<c>DurableInboxExtension</c> via keyed resolution; no test in this file exercises its
    /// persistence behavior (it is only required to make the dependency graph resolvable).
    /// </summary>
    private sealed class FakeDurableDictionary<TKey, TValue> : Dictionary<TKey, TValue>, IDurableDictionary<TKey, TValue> where TKey : notnull;

    /// <summary>
    /// Minimal <see cref="IDurableValue{T}"/> fake sufficient to satisfy DI construction of
    /// <c>DurableInboxExtension</c> via keyed resolution.
    /// </summary>
    private sealed class FakeDurableValue<T> : IDurableValue<T>
    {
        public T? Value { get; set; }
    }
}
