using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.Journaling.Json;
using Orleans.Metadata;
using Orleans.Providers;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT"), TestSuite("BVT"), TestProvider("None"), TestArea("Journaling")]
public sealed class GrainJournalStorageProviderTests(JournalCompositionFixture fixture) : IClassFixture<JournalCompositionFixture>
{
    [Fact]
    public async Task GrainProviders_IsolateAllStateAndRecoverWithOneManagerPerActivation()
    {
        var types = new[]
        {
            (typeof(DefaultProviderSelectionGrain), ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME),
            (typeof(NamedProviderSelectionGrainA), "provider-A"),
            (typeof(NamedProviderSelectionGrainB), "provider-B"),
            (typeof(InheritedProviderSelectionGrain), "provider-A")
        };
        var providers = new Dictionary<string, JournalCompositionStorageProvider>(StringComparer.Ordinal)
        {
            [ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME] = fixture.Storage,
            ["provider-A"] = Assert.IsType<JournalCompositionStorageProvider>(
                fixture.Services.GetRequiredKeyedService<IJournalStorageProvider>("provider-A")),
            ["provider-B"] = Assert.IsType<JournalCompositionStorageProvider>(
                fixture.Services.GetRequiredKeyedService<IJournalStorageProvider>("provider-B"))
        };
        var key = Guid.NewGuid();
        var grains = types.Select(entry => fixture.Client.GetGrain<IProviderSelectionGrain>(key, entry.Item1.FullName)).ToArray();
        var token = TestContext.Current.CancellationToken;
        for (var i = 0; i < grains.Length; i++)
        {
            Assert.Equal(new int[7], await grains[i].GetActivationValues());
            await grains[i].SetValues(i + 1);
        }

        for (var i = 0; i < grains.Length; i++)
        {
            var grain = grains[i];
            var expected = Enumerable.Repeat(i + 1, 7).ToArray();
            Assert.Equal(expected, await grain.GetValues());
            Assert.True(fixture.Cluster.TryGetGrainContext(grain.GetGrainId(), out var context));
            var manager = context.ActivationServices.GetRequiredService<IJournaledStateManager>();
            Assert.Same(manager, context.ActivationServices.GetRequiredService<IDurableStateManager>());
            var storage = providers[types[i].Item2].Get(context.GrainId);
            Assert.Equal(1, storage.Managers);
            Assert.Equal(1, storage.Reads);
            Assert.Equal(1, storage.Writes);
            foreach (var (name, provider) in providers)
            {
                Assert.Equal(name == types[i].Item2, provider.Contains(JournalId.FromGrainId(context.GrainId)));
            }

            context.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Provider selection recovery test"), token);
            await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), token);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.InitializeAsync(token).AsTask());
            Assert.Equal(expected, await grain.GetActivationValues());
            Assert.Equal(expected, await grain.GetValues());
            Assert.True(fixture.Cluster.TryGetGrainContext(grain.GetGrainId(), out var replacement));
            Assert.NotSame(manager, replacement.ActivationServices.GetRequiredService<IJournaledStateManager>());
            Assert.Equal(2, storage.Managers);
            Assert.Equal(2, storage.Reads);
            replacement.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Provider selection test complete"), token);
            await replacement.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), token);
        }
    }

    [Fact]
    public async Task NamedSelection_UsesSelectedProviderWithoutDefaultRegistration()
    {
        var grainType = GrainType.Create("named-selection");
        var builder = CreateBuilder(JournalingTestBase.CreateGrainPropertiesResolver((typeof(NamedProviderSelectionGrainA), grainType)));
        builder.AddVolatileJournalStorage("provider-A").AddVolatileJournalStorage("provider-B");
        var id = GrainId.Create(grainType, "key");
        AddGrainContext(builder.Services, id);
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        var token = TestContext.Current.CancellationToken;
        await using (var scope = services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
            var value = scope.ServiceProvider.GetRequiredKeyedService<IDurableValue<int>>("value");
            Assert.Same(manager, scope.ServiceProvider.GetRequiredService<IDurableStateManager>());
            Assert.Null(services.GetService<IJournalStorageProvider>());
            var lifecycle = scope.ServiceProvider.GetRequiredService<CompositionTestLifecycle>();
            Assert.Equal(1, lifecycle.Subscriptions);
            await lifecycle.OnStart(token);
            value.Value = 42;
            await manager.WriteStateAsync(token);
        }

        var factory = services.GetRequiredKeyedService<IJournaledStateManagerFactory>("provider-A");
        await using var recovered = factory.CreateStandalone(JournalId.FromGrainId(id));
        var codec = services.GetRequiredKeyedService<IDurableValueCommandCodec<int>>(JsonLinesJournalFormat.JournalFormatKey);
        var recoveredValue = new DurableValue<int>("value", recovered, codec);
        await recovered.InitializeAsync(token);
        Assert.Equal(42, recoveredValue.Value);
        Assert.Null(await services.GetRequiredKeyedService<IJournalStorageProvider>("provider-B")
            .CreateStorage(JournalId.FromGrainId(id)).GetMetadataAsync(token));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DefaultSelection_PreservesUnkeyedOverrideAndRegistrationOrder(bool registerDefaultFirst, bool explicitDefault)
    {
        var grainType = GrainType.Create("default-selection");
        var builder = CreateBuilder(JournalingTestBase.CreateGrainPropertiesResolver(
            (explicitDefault ? typeof(ExplicitDefaultProviderSelectionGrain) : typeof(DefaultProviderSelectionGrain), grainType)));
        var replacement = Substitute.For<IJournalStorageProvider>();
        replacement.CreateStorage(Arg.Any<JournalId>()).Returns(new VolatileJournalStorage());
        builder.Services.AddSingleton(replacement);
        if (registerDefaultFirst) builder.AddVolatileJournalStorage();
        builder.AddVolatileJournalStorage("provider-A").AddVolatileJournalStorage("provider-B");
        if (!registerDefaultFirst) builder.AddVolatileJournalStorage();
        var id = GrainId.Create(grainType, "key");
        AddGrainContext(builder.Services, id);
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        await using var scope = services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
        Assert.Same(manager, scope.ServiceProvider.GetRequiredService<IDurableStateManager>());
        Assert.Same(replacement, services.GetRequiredKeyedService<IJournalStorageProvider>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
        replacement.Received(1).CreateStorage(JournalId.FromGrainId(id));
        var token = TestContext.Current.CancellationToken;
        foreach (var name in new[] { "provider-A", "provider-B" })
        {
            Assert.Null(await services.GetRequiredKeyedService<IJournalStorageProvider>(name)
                .CreateStorage(JournalId.FromGrainId(id)).GetMetadataAsync(token));
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("provider-a")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)]
    public async Task InvalidSelection_ReportsGrainAndProviderBeforeCreatingStorage(string name)
    {
        var grainType = GrainType.Create("invalid-selection");
        var properties = new GrainProperties(ImmutableDictionary<string, string>.Empty
            .WithComparers(StringComparer.Ordinal).Add(JournalStorageProviderAttribute.PropertyKey, name));
        var manifest = new GrainManifest(
            ImmutableDictionary<GrainType, GrainProperties>.Empty.Add(grainType, properties),
            ImmutableDictionary<GrainInterfaceType, GrainInterfaceProperties>.Empty);
        var manifestProvider = Substitute.For<IClusterManifestProvider>();
        manifestProvider.Current.Returns(new ClusterManifest(default, ImmutableDictionary<SiloAddress, GrainManifest>.Empty, [manifest]));
        var builder = CreateBuilder(new GrainPropertiesResolver(manifestProvider));
        var provider = Substitute.For<IJournalStorageProvider>();
        builder.AddJournalStorage("provider-A", _ => provider);
        if (name != ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME) builder.Services.AddSingleton(provider);
        AddGrainContext(builder.Services, GrainId.Create(grainType, "key"));
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        await using var scope = services.CreateAsyncScope();
        var exception = Assert.Throws<OrleansConfigurationException>(() => scope.ServiceProvider.GetRequiredService<IJournaledStateManager>());
        Assert.Contains($"'{name}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"'{grainType}'", exception.Message, StringComparison.Ordinal);
        provider.DidNotReceive().CreateStorage(Arg.Any<JournalId>());
        Assert.Equal(0, scope.ServiceProvider.GetRequiredService<CompositionTestLifecycle>().Subscriptions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Attribute_InvalidNameReportsGrainType(string? name)
    {
        var grainType = GrainType.Create("invalid-attribute");
        var attribute = new JournalStorageProviderAttribute(name!);
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        var exception = Assert.Throws<OrleansConfigurationException>(() =>
            attribute.Populate(fixture.Services, typeof(DefaultProviderSelectionGrain), grainType, properties));
        Assert.Contains(typeof(DefaultProviderSelectionGrain).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains($"'{grainType}'", exception.Message, StringComparison.Ordinal);
        Assert.Empty(properties);
    }

    private static ProviderSelectionSiloBuilder CreateBuilder(GrainPropertiesResolver resolver)
    {
        var builder = new ProviderSelectionSiloBuilder();
        builder.Services.AddLogging();
        builder.Services.AddSingleton(resolver);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, static (services, _) => services.GetRequiredService<TimeProvider>());
        builder.AddJournaling().UseJsonJournalFormat(JournalingTestsJsonContext.Default);
        return builder;
    }

    private static void AddGrainContext(IServiceCollection services, GrainId id)
    {
        services.AddScoped<CompositionTestLifecycle>();
        services.AddScoped<IGrainContext>(activationServices =>
        {
            var context = Substitute.For<IGrainContext>();
            context.GrainId.Returns(id);
            context.ActivationServices.Returns(activationServices);
            context.ObservableLifecycle.Returns(activationServices.GetRequiredService<CompositionTestLifecycle>());
            return context;
        });
    }

    private sealed class ProviderSelectionSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}

public interface IProviderSelectionGrain : IGrainWithGuidKey
{
    Task SetValues(int value);
    Task<int[]> GetValues();
    Task<int[]> GetActivationValues();
}

public abstract class ProviderSelectionGrain : DurableGrain, IProviderSelectionGrain
{
    private readonly IDurableValue<int> _value;
    private readonly IDurableList<int> _list;
    private readonly IDurableDictionary<string, int> _dictionary;
    private readonly IDurableQueue<int> _queue;
    private readonly IDurableSet<int> _set;
    private readonly IPersistentState<int> _persistent;
    private readonly IDurableValue<int> _programmatic;
    private int[] _activationValues = [];

    protected ProviderSelectionGrain(
        [FromKeyedServices("value")] IDurableValue<int> value,
        [FromKeyedServices("list")] IDurableList<int> list,
        [FromKeyedServices("dictionary")] IDurableDictionary<string, int> dictionary,
        [FromKeyedServices("queue")] IDurableQueue<int> queue,
        [FromKeyedServices("set")] IDurableSet<int> set,
        [FromKeyedServices("persistent")] IPersistentState<int> persistent)
    {
        _value = value;
        _list = list;
        _dictionary = dictionary;
        _queue = queue;
        _set = set;
        _persistent = persistent;
        _programmatic = StateManager.GetOrAddValue<int>("programmatic");
        Assert.Same(StateManager, ServiceProvider.GetRequiredService<IJournaledStateManager>());
        Assert.Same(value, StateManager.GetOrAddValue<int>("value"));
        Assert.Same(list, StateManager.GetOrAddList<int>("list"));
        Assert.Same(dictionary, StateManager.GetOrAddDictionary<string, int>("dictionary"));
        Assert.Same(queue, StateManager.GetOrAddQueue<int>("queue"));
        Assert.Same(set, StateManager.GetOrAddSet<int>("set"));
        Assert.Same(persistent, StateManager.GetOrAddPersistentState<int>("persistent"));
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _activationValues = Read();
        return Task.CompletedTask;
    }

    public async Task SetValues(int value)
    {
        _value.Value = value;
        _list.Add(value);
        _dictionary["value"] = value;
        _queue.Enqueue(value);
        _set.Add(value);
        _persistent.State = value;
        _programmatic.Value = value;
        await WriteStateAsync();
    }

    public Task<int[]> GetValues() => Task.FromResult(Read());
    public Task<int[]> GetActivationValues() => Task.FromResult(_activationValues);
    private int[] Read() =>
        [_value.Value, _list.FirstOrDefault(), _dictionary.TryGetValue("value", out var value) ? value : 0, _queue.FirstOrDefault(),
            _set.SingleOrDefault(), _persistent.RecordExists ? _persistent.State : 0, _programmatic.Value];
}

public sealed class DefaultProviderSelectionGrain(
    [FromKeyedServices("value")] IDurableValue<int> value,
    [FromKeyedServices("list")] IDurableList<int> list,
    [FromKeyedServices("dictionary")] IDurableDictionary<string, int> dictionary,
    [FromKeyedServices("queue")] IDurableQueue<int> queue,
    [FromKeyedServices("set")] IDurableSet<int> set,
    [FromKeyedServices("persistent")] IPersistentState<int> persistent)
    : ProviderSelectionGrain(value, list, dictionary, queue, set, persistent);

[JournalStorageProvider("Default")]
public sealed class ExplicitDefaultProviderSelectionGrain(
    [FromKeyedServices("value")] IDurableValue<int> value,
    [FromKeyedServices("list")] IDurableList<int> list,
    [FromKeyedServices("dictionary")] IDurableDictionary<string, int> dictionary,
    [FromKeyedServices("queue")] IDurableQueue<int> queue,
    [FromKeyedServices("set")] IDurableSet<int> set,
    [FromKeyedServices("persistent")] IPersistentState<int> persistent)
    : ProviderSelectionGrain(value, list, dictionary, queue, set, persistent);

[JournalStorageProvider("provider-A")]
public class NamedProviderSelectionGrainA(
    [FromKeyedServices("value")] IDurableValue<int> value,
    [FromKeyedServices("list")] IDurableList<int> list,
    [FromKeyedServices("dictionary")] IDurableDictionary<string, int> dictionary,
    [FromKeyedServices("queue")] IDurableQueue<int> queue,
    [FromKeyedServices("set")] IDurableSet<int> set,
    [FromKeyedServices("persistent")] IPersistentState<int> persistent)
    : ProviderSelectionGrain(value, list, dictionary, queue, set, persistent);

[JournalStorageProvider("provider-B")]
public sealed class NamedProviderSelectionGrainB(
    [FromKeyedServices("value")] IDurableValue<int> value,
    [FromKeyedServices("list")] IDurableList<int> list,
    [FromKeyedServices("dictionary")] IDurableDictionary<string, int> dictionary,
    [FromKeyedServices("queue")] IDurableQueue<int> queue,
    [FromKeyedServices("set")] IDurableSet<int> set,
    [FromKeyedServices("persistent")] IPersistentState<int> persistent)
    : NamedProviderSelectionGrainA(value, list, dictionary, queue, set, persistent);

public sealed class InheritedProviderSelectionGrain(
    [FromKeyedServices("value")] IDurableValue<int> value,
    [FromKeyedServices("list")] IDurableList<int> list,
    [FromKeyedServices("dictionary")] IDurableDictionary<string, int> dictionary,
    [FromKeyedServices("queue")] IDurableQueue<int> queue,
    [FromKeyedServices("set")] IDurableSet<int> set,
    [FromKeyedServices("persistent")] IPersistentState<int> persistent)
    : NamedProviderSelectionGrainA(value, list, dictionary, queue, set, persistent);
