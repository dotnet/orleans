using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace Tester.ClientConnectionTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT")]
public class ClientObserverRoutingTests
{
    public static ClientNotAvailableException CreateUnavailableClientException()
        => new(ClientGrainId.Create().GrainId);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentlyStaleClientRoutesRefreshGatewayOwners(bool unavailable)
    {
        var builder = new InProcessTestClusterBuilder(3);
        builder.ConfigureHost(hostBuilder => TestDefaultConfiguration.ConfigureHostConfiguration(hostBuilder.Configuration));
        builder.ConfigureSilo((_, siloBuilder) => siloBuilder.ConfigureServices(services =>
        {
            services.AddSingleton<ControlledConnectedClients>(services =>
                new(Assert.IsType<Gateway>(services.GetRequiredService<MessageCenter>().Gateway)));
            services.AddSingleton<IConnectedClientCollection>(services => services.GetRequiredService<ControlledConnectedClients>());
            services.AddSingleton<RedirectOnceClientDirectory>();
            services.AddSingleton<ILocalClientDirectory>(services => services.GetRequiredService<RedirectOnceClientDirectory>());
        }));
        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        var sender = cluster.Silos[0].ServiceProvider;
        var receiver = cluster.Silos[2].ServiceProvider;
        var observer = new EchoObserver();
        var target = unavailable
            ? ObserverGrainId.Create(ClientGrainId.Create()).GrainId
            : receiver.GetRequiredService<IGrainFactory>().CreateObjectReference<IClientRoutingObserver>(observer).GetGrainId();
        Assert.True(ClientGrainId.TryParse(target, out var clientId));
        var directories = cluster.Silos.Select(silo => silo.ServiceProvider.GetRequiredService<ClientDirectory>()).ToArray();
        var connections = cluster.Silos.Select(silo => silo.ServiceProvider.GetRequiredService<ControlledConnectedClients>()).ToArray();

        // Hold publication while real directories exchange the connected snapshots, then drop the
        // simulated gateway connections. Each replica retains the other gateways' stale routes.
        foreach (var directory in directories)
        {
            new ClientDirectory.TestAccessor(directory).SchedulePublishUpdate = static () => { };
        }

        foreach (var connection in connections)
        {
            connection.SetAdditionalClients([clientId.GrainId]);
        }

        var routes = ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId>, long)>.Empty;
        for (var i = 0; i < directories.Length; i++)
        {
            var snapshot = await directories[i].GetClientRoutes(
                ImmutableDictionary<SiloAddress, long>.Empty, TestContext.Current.CancellationToken);
            var silo = cluster.Silos[i].SiloAddress;
            routes = routes.Add(silo, snapshot[silo]);
        }

        foreach (var directory in directories)
        {
            await directory.OnUpdateClientRoutes(routes, TestContext.Current.CancellationToken);
        }

        foreach (var connection in connections)
        {
            connection.SetAdditionalClients([]);
        }

        for (var i = 0; i < directories.Length; i++)
        {
            Assert.True(directories[i].TryLocalLookup(clientId.GrainId, out var staleRoutes));
            var expectedSilos = cluster.Silos
                .Where((_, index) => index != i || !unavailable && index == 2)
                .Select(silo => silo.SiloAddress)
                .OrderBy(silo => silo.ToString());
            Assert.Equal(expectedSilos, staleRoutes.Select(route => route.SiloAddress!).OrderBy(silo => silo.ToString()));
        }

        var redirect = sender.GetRequiredService<RedirectOnceClientDirectory>();
        redirect.Arm(clientId.GrainId, cluster.Silos[0].SiloAddress);
        var reference = sender.GetRequiredService<IGrainFactory>().GetGrain<IClientRoutingObserver>(target);
        if (unavailable)
        {
            await Assert.ThrowsAsync<ClientNotAvailableException>(
                () => reference.Echo(42).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            Assert.Empty(await directories[0].Lookup(clientId.GrainId));
            Assert.Equal(0, observer.CallCount);
        }
        else
        {
            Assert.Equal(42, await reference.Echo(42).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            var liveRoute = Assert.Single(await directories[0].Lookup(clientId.GrainId));
            Assert.Equal(cluster.Silos[2].SiloAddress, liveRoute.SiloAddress);
            Assert.Equal(43, await reference.Echo(43).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            Assert.Equal(2, observer.CallCount);
        }

        Assert.Equal(1, redirect.StaleRoutesReturned);
        foreach (var silo in cluster.Silos)
        {
            Assert.Null(silo.ServiceProvider.GetRequiredService<ActivationDirectory>().FindTarget(target));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleClientRouteIsResolvedThroughClientDirectory(bool unavailable)
    {
        var builder = new InProcessTestClusterBuilder(2);
        builder.ConfigureHost(hostBuilder => TestDefaultConfiguration.ConfigureHostConfiguration(hostBuilder.Configuration));
        builder.ConfigureSilo((_, siloBuilder) => siloBuilder.ConfigureServices(services =>
        {
            services.AddSingleton<RedirectOnceClientDirectory>();
            services.AddSingleton<ILocalClientDirectory>(services => services.GetRequiredService<RedirectOnceClientDirectory>());
        }));
        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        var sender = cluster.Silos[0].ServiceProvider;
        var receiver = cluster.Silos[1].ServiceProvider;
        var observer = new EchoObserver();
        var target = unavailable
            ? ObserverGrainId.Create(ClientGrainId.Create()).GrainId
            : receiver.GetRequiredService<IGrainFactory>().CreateObjectReference<IClientRoutingObserver>(observer).GetGrainId();
        Assert.True(ClientGrainId.TryParse(target, out var clientId));
        var directory = sender.GetRequiredService<RedirectOnceClientDirectory>();
        directory.Arm(clientId.GrainId, cluster.Silos[0].SiloAddress);
        var reference = sender.GetRequiredService<IGrainFactory>().GetGrain<IClientRoutingObserver>(target);

        if (unavailable)
        {
            await Assert.ThrowsAsync<ClientNotAvailableException>(
                () => reference.Echo(42).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            Assert.Equal(0, observer.CallCount);
        }
        else
        {
            Assert.Equal(42, await reference.Echo(42).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
            Assert.Equal(1, observer.CallCount);
        }

        Assert.Equal(1, directory.StaleRoutesReturned);
        Assert.Null(sender.GetRequiredService<ActivationDirectory>().FindTarget(target));
        Assert.Null(receiver.GetRequiredService<ActivationDirectory>().FindTarget(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableClientReturnsNoLocalActivation(bool observer)
    {
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureHost(hostBuilder => TestDefaultConfiguration.ConfigureHostConfiguration(hostBuilder.Configuration));
        await using var cluster = builder.Build();
        await cluster.DeployAsync(TestContext.Current.CancellationToken);

        var clientId = ClientGrainId.Create();
        var grainId = observer ? ObserverGrainId.Create(clientId).GrainId : clientId.GrainId;
        var catalog = cluster.Silos[0].ServiceProvider.GetRequiredService<Catalog>();

        // An absent local client route is handled by client placement, which can
        // find another gateway or report ClientNotAvailableException to the caller.
        Assert.Null(catalog.GetOrCreateActivation(grainId, requestContextData: null, rehydrationContext: null));
        Assert.Null(cluster.Silos[0].ServiceProvider.GetRequiredService<ActivationDirectory>().FindTarget(grainId));
    }

    private sealed class EchoObserver : IClientRoutingObserver
    {
        public int CallCount { get; private set; }

        public Task<int> Echo(int value)
        {
            CallCount++;
            return Task.FromResult(value);
        }
    }

    private sealed class ControlledConnectedClients(IConnectedClientCollection inner) : IConnectedClientCollection
    {
        private ImmutableList<GrainId> _additionalClients = [];
        private long _version;

        public long Version => inner.Version + Interlocked.Read(ref _version);

        public List<GrainId> GetConnectedClientIds()
        {
            var clients = inner.GetConnectedClientIds();
            clients.AddRange(Volatile.Read(ref _additionalClients));
            return clients;
        }

        public void SetAdditionalClients(ImmutableList<GrainId> clients)
        {
            Volatile.Write(ref _additionalClients, clients);
            Interlocked.Increment(ref _version);
        }
    }

    private sealed class RedirectOnceClientDirectory(ClientDirectory inner) : ILocalClientDirectory
    {
        private GrainId _clientId;
        private GrainAddress? _staleAddress;
        public int StaleRoutesReturned { get; private set; }

        public void Arm(GrainId clientId, SiloAddress siloAddress)
        {
            _clientId = clientId;
            _staleAddress = GrainAddress.GetAddress(siloAddress, clientId, ActivationId.NewId());
        }

        public bool TryLocalLookup(GrainId grainId, [NotNullWhen(true)] out List<GrainAddress>? addresses)
        {
            if (grainId == _clientId && Interlocked.Exchange(ref _staleAddress, null) is { } stale)
            {
                StaleRoutesReturned++;
                addresses = [stale];
                return true;
            }

            return inner.TryLocalLookup(grainId, out addresses);
        }

        public ValueTask<List<GrainAddress>> Lookup(GrainId grainId) => inner.Lookup(grainId);

        public void InvalidateCache(GrainId grainId) => inner.InvalidateCache(grainId);
    }
}

public interface IClientRoutingObserver : IGrainObserver
{
    Task<int> Echo(int value);
}
