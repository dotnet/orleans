using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
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
    }
}

public interface IClientRoutingObserver : IGrainObserver
{
    Task<int> Echo(int value);
}
