using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableTasks;
using Orleans.Hosting;
using Orleans.Journaling.DurableTasks;
using Orleans.Runtime.DurableTasks;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests for <see cref="Orleans.Journaling.DurableTasks.DurableTaskHostingExtensions"/>: the ISiloBuilder
/// extension methods that register the volatile and journaled implementations of
/// <see cref="IDurableTaskGrainStorage"/>.
/// </summary>
[TestCategory("BVT")]
public class DurableTaskJournalingHostingExtensionsTests
{
    [Fact]
    public void AddVolatileDurableTaskStorage_RegistersVolatileDurableTaskGrainStorageAsTransient()
    {
        var builder = new TestSiloBuilder();

        var result = builder.AddVolatileDurableTaskStorage();

        Assert.Same(builder, result);
        var descriptor = Assert.Single(builder.Services, d => d.ServiceType == typeof(VolatileDurableTaskGrainStorage));
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
        Assert.Null(descriptor.ImplementationInstance);
        Assert.Null(descriptor.ImplementationFactory);
        Assert.Equal(typeof(VolatileDurableTaskGrainStorage), descriptor.ImplementationType);
    }

    [Fact]
    public void AddVolatileDurableTaskStorage_ForwardsIDurableTaskGrainStorageAsTransient()
    {
        var builder = new TestSiloBuilder();

        builder.AddVolatileDurableTaskStorage();

        var descriptor = Assert.Single(builder.Services, d => d.ServiceType == typeof(IDurableTaskGrainStorage));
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
        Assert.NotNull(descriptor.ImplementationFactory);
        Assert.Null(descriptor.ImplementationType);
    }

    [Fact]
    public void AddVolatileDurableTaskStorage_IDurableTaskGrainStorage_ResolvesToVolatileDurableTaskGrainStorageInstance()
    {
        var builder = new TestSiloBuilder();
        builder.Services.AddSerializer();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.AddVolatileDurableTaskStorage();

        using var provider = builder.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var concrete = scope.ServiceProvider.GetRequiredService<VolatileDurableTaskGrainStorage>();
        var forwarded = scope.ServiceProvider.GetRequiredService<IDurableTaskGrainStorage>();

        // AddFromExisting<TService,TImplementation> resolves TService by delegating to
        // sp.GetRequiredService(typeof(TImplementation)); because both registrations are transient, this
        // yields two distinct instances (transient forwarding does not imply same-instance sharing), unlike
        // a scoped/singleton forwarding. We assert the forwarded value is of the correct concrete type, and
        // that repeated transient resolution is genuinely a new instance each time.
        Assert.IsType<VolatileDurableTaskGrainStorage>(forwarded);
        Assert.NotSame(concrete, forwarded);

        var concreteAgain = scope.ServiceProvider.GetRequiredService<VolatileDurableTaskGrainStorage>();
        Assert.NotSame(concrete, concreteAgain);
    }

    [Fact]
    public void AddJournaledDurableTaskStorage_RegistersDurableTaskGrainStorageAsScoped()
    {
        var builder = new TestSiloBuilder();

        var result = builder.AddJournaledDurableTaskStorage();

        Assert.Same(builder, result);
        var descriptor = Assert.Single(builder.Services, d => d.ServiceType == typeof(DurableTaskGrainStorage));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.Equal(typeof(DurableTaskGrainStorage), descriptor.ImplementationType);
    }

    [Fact]
    public void AddJournaledDurableTaskStorage_ForwardsIDurableTaskGrainStorageAsScoped()
    {
        var builder = new TestSiloBuilder();

        builder.AddJournaledDurableTaskStorage();

        var descriptor = Assert.Single(builder.Services, d => d.ServiceType == typeof(IDurableTaskGrainStorage));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.NotNull(descriptor.ImplementationFactory);
        Assert.Null(descriptor.ImplementationType);
    }

    [Fact]
    public void AddJournaledDurableTaskStorage_UsesTryAddScoped_SoCallingItTwiceDoesNotDuplicateRegistration()
    {
        var builder = new TestSiloBuilder();

        builder.AddJournaledDurableTaskStorage();
        builder.AddJournaledDurableTaskStorage();

        // TryAddScoped only registers if no existing ServiceDescriptor for DurableTaskGrainStorage exists;
        // calling AddJournaledDurableTaskStorage twice must therefore not produce duplicate registrations.
        Assert.Single(builder.Services, d => d.ServiceType == typeof(DurableTaskGrainStorage));

        // AddFromExisting<IDurableTaskGrainStorage, DurableTaskGrainStorage> uses non-Try Add, so calling
        // AddJournaledDurableTaskStorage twice DOES append a second forwarding registration for the service
        // interface (only the concrete-type registration is deduplicated via TryAddScoped).
        Assert.Equal(2, builder.Services.Count(d => d.ServiceType == typeof(IDurableTaskGrainStorage)));
    }

    [Fact]
    public void AddVolatileDurableTaskStorage_CalledFirst_ThenAddJournaledDurableTaskStorage_BothConcreteTypesRegistered_LastForwardingWins()
    {
        // This is the real regression-guard test: AddVolatileDurableTaskStorage uses a plain AddTransient (not
        // Try-prefixed) for VolatileDurableTaskGrainStorage, while AddJournaledDurableTaskStorage uses
        // TryAddScoped for DurableTaskGrainStorage. Both extensions forward IDurableTaskGrainStorage via
        // AddFromExisting, which itself uses a plain (non-Try) Add. Since IServiceCollection resolves the
        // LAST registration for a given service type when resolving a single instance via GetRequiredService,
        // whichever AddXxxDurableTaskStorage call happens LAST determines which concrete type
        // IDurableTaskGrainStorage resolves to - regardless of Try* semantics on the concrete-type
        // registrations themselves.
        var builder = new TestSiloBuilder();
        builder.Services.AddSerializer();
        builder.Services.AddSingleton(TimeProvider.System);

        builder.AddVolatileDurableTaskStorage();
        builder.AddJournaledDurableTaskStorage();

        // Both concrete-type registrations exist independently; neither Add call clobbers the other's
        // concrete-type registration because they target different concrete types.
        Assert.Single(builder.Services, d => d.ServiceType == typeof(VolatileDurableTaskGrainStorage));
        Assert.Single(builder.Services, d => d.ServiceType == typeof(DurableTaskGrainStorage));

        // But IDurableTaskGrainStorage has two forwarding registrations, and single-instance resolution
        // returns the last one registered - i.e. the journaled implementation "wins" when
        // AddJournaledDurableTaskStorage is called after AddVolatileDurableTaskStorage.
        Assert.Equal(2, builder.Services.Count(d => d.ServiceType == typeof(IDurableTaskGrainStorage)));
        var lastDescriptor = builder.Services.Last(d => d.ServiceType == typeof(IDurableTaskGrainStorage));
        Assert.Equal(ServiceLifetime.Scoped, lastDescriptor.Lifetime);
    }

    [Fact]
    public void AddJournaledDurableTaskStorage_CalledFirst_ThenAddVolatileDurableTaskStorage_VolatileForwardingWinsLast()
    {
        // Symmetric ordering: when AddVolatileDurableTaskStorage is called SECOND, its transient forwarding
        // registration for IDurableTaskGrainStorage is appended last, so it wins single-instance resolution -
        // demonstrating that registration order (not the Try*/non-Try nature of the concrete-type
        // registrations) is what actually determines which implementation "wins" for the forwarded interface.
        var builder = new TestSiloBuilder();
        builder.Services.AddSerializer();
        builder.Services.AddSingleton(TimeProvider.System);

        builder.AddJournaledDurableTaskStorage();
        builder.AddVolatileDurableTaskStorage();

        Assert.Equal(2, builder.Services.Count(d => d.ServiceType == typeof(IDurableTaskGrainStorage)));
        var lastDescriptor = builder.Services.Last(d => d.ServiceType == typeof(IDurableTaskGrainStorage));
        Assert.Equal(ServiceLifetime.Transient, lastDescriptor.Lifetime);

        // Resolve to prove it concretely: since AddVolatileDurableTaskStorage's forwarding registration was
        // added last, GetRequiredService<IDurableTaskGrainStorage>() must yield a VolatileDurableTaskGrainStorage
        // instance - the opposite of the previous test's ordering.
        using var provider = builder.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var resolved = scope.ServiceProvider.GetRequiredService<IDurableTaskGrainStorage>();
        Assert.IsType<VolatileDurableTaskGrainStorage>(resolved);
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
