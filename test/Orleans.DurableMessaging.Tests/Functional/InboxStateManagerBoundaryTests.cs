using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Journaling.Json;
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
    public async Task ActivationStandardState_UsesScopedCodecAndSingleOwnerEnrollment(bool keyedFirst)
    {
        const string selectedFormat = "orleans-binary";
        var builder = CreateBuilder(selectedFormat);
        var codecType = GetDictionaryCodecType(builder.Services, selectedFormat);
        builder.Services.AddKeyedScoped<IDurableDictionaryCommandCodec<string, int>>(selectedFormat, (sp, _) =>
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
        builder.Services.AddScoped(sp => new OwnedComponent(
            sp.GetRequiredKeyedService<IDurableDictionary<string, int>>("state"), sp.GetRequiredService<Dependency>()));
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        IDurableDictionary<string, int> state;
        OwnedComponent component;
        Dependency dependency;
        IJournaledStateManager journalOwner;
        await using (var scope = services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            var app = sp.GetRequiredService<IDurableStateManager>();
            journalOwner = sp.GetRequiredService<IJournaledStateManager>();
            Assert.Same(app, journalOwner);
            var codec = Assert.IsType<TrackingCodec>(sp.GetRequiredKeyedService<IDurableDictionaryCommandCodec<string, int>>(selectedFormat));
            Assert.Throws<InvalidOperationException>(() => services.GetRequiredKeyedService<IDurableDictionaryCommandCodec<string, int>>(selectedFormat));
            state = keyedFirst
                ? sp.GetRequiredKeyedService<IDurableDictionary<string, int>>("state")
                : app.GetOrAddState<IDurableDictionary<string, int>>("state");
            component = sp.GetRequiredService<OwnedComponent>();
            dependency = sp.GetRequiredService<Dependency>();
            Assert.Same(state, component.State);
            Assert.Same(dependency, component.Dependency);
            Assert.Same(state, sp.GetRequiredKeyedService<IDurableDictionary<string, int>>("state"));
            Assert.Same(state, app.GetOrAddState<IDurableDictionary<string, int>>("state"));
            Assert.True(app.TryGetState<IDurableDictionary<string, int>>("state", out var found));
            Assert.Same(state, found);
            Assert.True(journalOwner.TryGetStateMachine("state", out var machine));
            Assert.Same(state, machine);
            sp.GetRequiredService<IGrainContext>().ObservableLifecycle.Received(1)
                .Subscribe(Arg.Any<string>(), GrainLifecycleStage.SetupState, Arg.Any<ILifecycleObserver>());
            await journalOwner.InitializeAsync(Cancellation);
            Assert.Equal(0, codec.Applies);
            state.Add("committed", 17);
            Assert.Equal(1, codec.SetsWritten);
            await app.WriteStateAsync(Cancellation);
            Assert.Equal(1, codec.SetsWritten);
            Assert.Equal(17, state["committed"]);
            Assert.Throws<InvalidOperationException>(() => app.GetOrAddState<IDurableDictionary<string, int>>("late"));
            Assert.Throws<InvalidOperationException>(() => sp.GetRequiredKeyedService<IDurableValue<int>>("late"));
            Assert.Same(state, app.GetOrAddState<IDurableDictionary<string, int>>("state"));
            await journalOwner.DeleteStateAsync(Cancellation);
            Assert.Empty(state);
            Assert.False(dependency.Disposed);
            Assert.Equal(0, component.Disposals);
            await using var other = services.CreateAsyncScope();
            var otherOwner = other.ServiceProvider.GetRequiredService<IJournaledStateManager>();
            Assert.NotSame(journalOwner, otherOwner);
            Assert.NotSame(codec, other.ServiceProvider.GetRequiredKeyedService<IDurableDictionaryCommandCodec<string, int>>(selectedFormat));
        }
        Assert.True(dependency.Disposed);
        Assert.Equal(1, component.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => journalOwner.WriteStateAsync(Cancellation).AsTask());
    }

    [Fact]
    public async Task NamedStandaloneOwner_UsesSelectedFormatAndCallerOwnedComponentsAcrossReplay()
    {
        const string selectedFormat = "json";
        const string storageName = "caller-owned-store";
        var builder = CreateBuilder(selectedFormat);
        builder.AddVolatileJournalStorage(storageName);
        builder.UseJsonJournalFormat(StandardStateJsonContext.Default);
        var codecType = GetDictionaryCodecType(builder.Services, selectedFormat);
        builder.Services.AddKeyedSingleton<IDurableDictionaryCommandCodec<string, int>>(selectedFormat, (sp, _) =>
            new TrackingCodec((IDurableDictionaryCommandCodec<string, int>)ActivatorUtilities.CreateInstance(sp, codecType)));
        var dependenciesCreated = 0;
        builder.Services.AddScoped(_ => { dependenciesCreated++; return new Dependency(); });
        var id = new JournalId("inbox-state-lifetime/" + Guid.NewGuid().ToString("N"));
        builder.Services.AddScoped<IJournaledStateManager>(sp =>
            sp.GetRequiredKeyedService<IJournaledStateManagerFactory>(storageName).CreateStandalone(id));
        builder.Services.AddScoped(sp => new OwnedComponent(
            sp.GetRequiredKeyedService<IDurableDictionary<string, int>>("state"), sp.GetRequiredService<Dependency>()));
        await using var services = builder.Services.BuildServiceProvider(validateScopes: true);
        Assert.Equal(0, dependenciesCreated);
        var codec = Assert.IsType<TrackingCodec>(services.GetRequiredKeyedService<IDurableDictionaryCommandCodec<string, int>>(selectedFormat));
        Dependency dependency;
        OwnedComponent component;
        await using (var scope = services.CreateAsyncScope())
        {
            var first = scope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
            Assert.False(first is IDurableStateManager);
            dependency = scope.ServiceProvider.GetRequiredService<Dependency>();
            Assert.False(first.TryGetStateMachine("state", out _));
            component = scope.ServiceProvider.GetRequiredService<OwnedComponent>();
            var state = component.State;
            Assert.Same(scope.ServiceProvider.GetRequiredKeyedService<IDurableDictionary<string, int>>("state"), state);
            Assert.True(first.TryGetStateMachine("state", out var registered));
            Assert.Same(state, registered);
            await first.InitializeAsync(Cancellation);
            Assert.Equal(0, codec.Applies);
            state.Add("persisted", 23);
            await first.WriteStateAsync(Cancellation);
            Assert.Equal(1, codec.SetsWritten);
            await first.DisposeAsync();
            Assert.Equal(0, component.Disposals);
            Assert.False(dependency.Disposed);
            Assert.Equal(1, dependenciesCreated);
            await using var recoveryScope = services.CreateAsyncScope();
            var recovered = recoveryScope.ServiceProvider.GetRequiredService<IJournaledStateManager>();
            Assert.NotSame(first, recovered);
            Assert.False(recovered is IDurableStateManager);
            var recoveredCodec = recoveryScope.ServiceProvider.GetRequiredKeyedService<IDurableDictionaryCommandCodec<string, int>>(selectedFormat);
            Assert.Same(codec, recoveredCodec);
            var fresh = recoveryScope.ServiceProvider.GetRequiredKeyedService<IDurableDictionary<string, int>>("state");
            await recovered.InitializeAsync(Cancellation);
            Assert.NotSame(state, fresh);
            Assert.Equal(new KeyValuePair<string, int>("persisted", 23), Assert.Single(fresh));
            Assert.Equal(1, codec.Applies);
            await recovered.DeleteStateAsync(Cancellation);
            Assert.Empty(fresh);
            await recovered.DisposeAsync();
            Assert.Equal(0, component.Disposals);
            Assert.False(dependency.Disposed);
            Assert.Equal(1, dependenciesCreated);
        }

        Assert.True(dependency.Disposed);
        Assert.Equal(1, component.Disposals);
    }

    private static Type GetDictionaryCodecType(IServiceCollection services, string selectedFormat) => services.Last(entry =>
        entry.ServiceType == typeof(IDurableDictionaryCommandCodec<,>) && Equals(entry.ServiceKey, selectedFormat))
        .KeyedImplementationType!.MakeGenericType(typeof(string), typeof(int));

    internal static TestSiloBuilder CreateBuilder(string selectedFormat)
    {
        var builder = new TestSiloBuilder();
        builder.Services.AddSerializer();
        builder.Services.AddLogging();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, static (sp, _) => sp.GetRequiredService<TimeProvider>());
        builder.AddVolatileJournalStorage();
        builder.Services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = selectedFormat);
        return builder;
    }

    internal sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class Dependency : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class OwnedComponent(IDurableDictionary<string, int> state, Dependency dependency) : IDisposable
    {
        public IDurableDictionary<string, int> State { get; } = state;
        public Dependency Dependency { get; } = dependency;
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }

    private sealed class TrackingCodec(IDurableDictionaryCommandCodec<string, int> inner) : IDurableDictionaryCommandCodec<string, int>
    {
        public int SetsWritten { get; private set; }
        public int Applies { get; private set; }
        public void WriteSet(string key, int value, JournalStreamWriter writer) { SetsWritten++; inner.WriteSet(key, value, writer); }
        public void WriteRemove(string key, JournalStreamWriter writer) => inner.WriteRemove(key, writer);
        public void WriteClear(JournalStreamWriter writer) => inner.WriteClear(writer);
        public void WriteSnapshot(IReadOnlyCollection<KeyValuePair<string, int>> items, JournalStreamWriter writer) => inner.WriteSnapshot(items, writer);
        public void Apply(JournalBufferReader input, IDurableDictionaryCommandHandler<string, int> consumer)
        {
            Applies++;
            inner.Apply(input, consumer);
        }
    }
}
