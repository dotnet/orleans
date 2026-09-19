using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class InboxStateManagerBoundaryTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationFactory_RegistersCanonicalStateWithScopedCodecAndOneEnrollment(bool keyedFirst)
    {
        var builder = CreateBuilder();
        var codecType = GetBinaryDictionaryCodecType(builder.Services);
        builder.Services.AddKeyedScoped<IDurableDictionaryCommandCodec<string, int>>("orleans-binary", (sp, _) =>
            new TrackingCodec((IDurableDictionaryCommandCodec<string, int>)ActivatorUtilities.CreateInstance(sp, codecType)));
        builder.Services.AddScoped<Dependency>();
        builder.Services.AddScoped<IGrainContext>(sp =>
        {
            var context = Substitute.For<IGrainContext>();
            context.ActivationServices.Returns(sp);
            context.GrainId.Returns(GrainId.Create("codec-scope", Guid.NewGuid().ToString("N")));
            context.ObservableLifecycle.Returns(Substitute.For<IGrainLifecycle>());
            return context;
        });
        var creations = 0;
        builder.Services.AddStateMachine<IDurableDictionary<string, int>, OwnedDictionary>((sp, name) =>
        {
            var owner = sp.GetRequiredService<IJournaledStateManager>();
            Assert.False(owner.TryGetStateMachine(name, out _));
            creations++;
            return new OwnedDictionary(owner, sp.GetRequiredService<Dependency>());
        });
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        OwnedDictionary state;
        Dependency dependency;
        IJournaledStateManager journalOwner;
        await using (var scope = services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            var app = sp.GetRequiredService<IDurableStateManager>();
            journalOwner = sp.GetRequiredService<IJournaledStateManager>();
            Assert.Same(app, journalOwner);
            var codec = Assert.IsType<TrackingCodec>(journalOwner.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, int>>());
            Assert.Same(sp.GetRequiredKeyedService<IDurableDictionaryCommandCodec<string, int>>("orleans-binary"), codec);
            Assert.Throws<InvalidOperationException>(() => services.GetRequiredKeyedService<IDurableDictionaryCommandCodec<string, int>>("orleans-binary"));
            state = Assert.IsType<OwnedDictionary>(keyedFirst
                ? sp.GetRequiredKeyedService<IDurableDictionary<string, int>>("state")
                : app.GetOrAddState<IDurableDictionary<string, int>>("state"));
            dependency = sp.GetRequiredService<Dependency>();
            Assert.Same(dependency, state.Dependency);
            Assert.Same(state, sp.GetRequiredKeyedService<IDurableDictionary<string, int>>("state"));
            Assert.Same(state, app.GetOrAddState<IDurableDictionary<string, int>>("state"));
            Assert.True(app.TryGetState<IDurableDictionary<string, int>>("state", out var found));
            Assert.Same(state, found);
            Assert.True(journalOwner.TryGetStateMachine("state", out var machine));
            Assert.Same(state, machine);
            Assert.Equal(1, creations);
            sp.GetRequiredService<IGrainContext>().ObservableLifecycle.Received(1)
                .Subscribe(Arg.Any<string>(), GrainLifecycleStage.SetupState, Arg.Any<ILifecycleObserver>());
            await journalOwner.InitializeAsync(Cancellation);
            state.Add("committed", 17);
            Assert.Equal(0, codec.SetsWritten);
            await app.WriteStateAsync(Cancellation);
            Assert.Equal(1, codec.SetsWritten);
            Assert.Equal(17, state["committed"]);
            Assert.Throws<InvalidOperationException>(() => app.GetOrAddState<IDurableDictionary<string, int>>("late"));
            Assert.Throws<InvalidOperationException>(() => journalOwner.RegisterStateMachine("late", ReceiverTestServices.CreateDeferredValue<int>(journalOwner)));
            Assert.Same(state, app.GetOrAddState<IDurableDictionary<string, int>>("state"));
            await journalOwner.DeleteStateAsync(Cancellation);
            Assert.Empty(state);
            Assert.False(dependency.Disposed);
            Assert.Equal(0, state.Disposals);
            await using var other = services.CreateAsyncScope();
            var otherOwner = other.ServiceProvider.GetRequiredService<IJournaledStateManager>();
            Assert.NotSame(journalOwner, otherOwner);
            Assert.NotSame(codec, otherOwner.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, int>>());
        }
        Assert.True(dependency.Disposed);
        Assert.Equal(1, state.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => journalOwner.WriteStateAsync(Cancellation).AsTask());
    }

    [Fact]
    public async Task StandaloneOwner_UsesSharedCodecAndCallerOwnedComponentsAcrossReplay()
    {
        var builder = CreateBuilder();
        var codecType = GetBinaryDictionaryCodecType(builder.Services);
        builder.Services.AddKeyedSingleton<IDurableDictionaryCommandCodec<string, int>>("orleans-binary", (sp, _) =>
            new TrackingCodec((IDurableDictionaryCommandCodec<string, int>)ActivatorUtilities.CreateInstance(sp, codecType)));
        var dependenciesCreated = 0;
        builder.Services.AddScoped(_ => { dependenciesCreated++; return new Dependency(); });
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        var factory = services.GetRequiredService<IJournaledStateManagerFactory>();
        var id = new JournalId("inbox-state-lifetime/" + Guid.NewGuid().ToString("N"));
        await using var first = factory.CreateStandalone(id);
        Assert.False(first is IDurableStateManager);
        Assert.Equal(0, dependenciesCreated);
        var codec = services.GetRequiredKeyedService<IDurableDictionaryCommandCodec<string, int>>("orleans-binary");
        Assert.Same(codec, first.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, int>>());
        Dependency dependency;
        OwnedDictionary state;
        await using (var scope = services.CreateAsyncScope())
        {
            dependency = scope.ServiceProvider.GetRequiredService<Dependency>();
            state = new OwnedDictionary(first, dependency);
            Assert.False(first.TryGetStateMachine("__orleans.durable-messaging.inbox", out _));
            first.RegisterStateMachine("__orleans.durable-messaging.inbox", state);
            await first.InitializeAsync(Cancellation);
            state.Add("persisted", 23);
            await first.WriteStateAsync(Cancellation);
            await first.DisposeAsync();
            Assert.Equal(0, state.Disposals);
            Assert.False(dependency.Disposed);
            Assert.Equal(1, dependenciesCreated);
            await using var recovered = factory.CreateStandalone(id);
            Assert.Same(codec, recovered.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, int>>());
            var fresh = new OwnedDictionary(recovered, dependency);
            recovered.RegisterStateMachine("__orleans.durable-messaging.inbox", fresh);
            await recovered.InitializeAsync(Cancellation);
            Assert.NotSame(state, fresh);
            Assert.Equal(new KeyValuePair<string, int>("persisted", 23), Assert.Single(fresh));
            await recovered.DeleteStateAsync(Cancellation);
            Assert.Empty(fresh);
            fresh.Dispose();
            Assert.False(dependency.Disposed);
        }
        Assert.True(dependency.Disposed);
        Assert.Equal(0, state.Disposals);
        state.Dispose();
        Assert.Equal(1, state.Disposals);
    }

    private static Type GetBinaryDictionaryCodecType(IServiceCollection services) => services.Last(entry =>
        entry.ServiceType == typeof(IDurableDictionaryCommandCodec<,>) && Equals(entry.ServiceKey, "orleans-binary"))
        .KeyedImplementationType!.MakeGenericType(typeof(string), typeof(int));

    private static TestSiloBuilder CreateBuilder()
    {
        var builder = new TestSiloBuilder();
        builder.Services.AddSerializer();
        builder.Services.AddLogging();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, static (sp, _) => sp.GetRequiredService<TimeProvider>());
        builder.AddVolatileJournalStorage();
        builder.Services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = "orleans-binary");
        return builder;
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class Dependency : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class OwnedDictionary(IJournaledStateManager owner, Dependency dependency)
        : ObservedJournalDictionary<string, int>(owner, deferred: true), IDisposable
    {
        public Dependency Dependency { get; } = dependency;
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }

    private sealed class TrackingCodec(IDurableDictionaryCommandCodec<string, int> inner) : IDurableDictionaryCommandCodec<string, int>
    {
        public int SetsWritten { get; private set; }
        public void WriteSet(string key, int value, JournalStreamWriter writer) { SetsWritten++; inner.WriteSet(key, value, writer); }
        public void WriteRemove(string key, JournalStreamWriter writer) => inner.WriteRemove(key, writer);
        public void WriteClear(JournalStreamWriter writer) => inner.WriteClear(writer);
        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<string, int>> items, JournalStreamWriter writer) => inner.WriteSnapshot(items, writer);
        public void Apply(JournalBufferReader input, IDurableDictionaryCommandHandler<string, int> consumer) => inner.Apply(input, consumer);
    }
}
