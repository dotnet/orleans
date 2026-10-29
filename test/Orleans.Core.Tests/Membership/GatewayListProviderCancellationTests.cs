using System.Reflection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.TestingHost.InProcess;
using TestExtensions;
using Xunit;

namespace NonSilo.Tests.Membership;

[TestCategory("BVT"), TestCategory("Membership")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class GatewayListProviderCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdditiveAsyncApi_PreservesUnobsoletedLegacySignature(bool initialize)
    {
        var legacyName = initialize ? nameof(IGatewayListProvider.InitializeGatewayListProvider) : nameof(IGatewayListProvider.GetGateways);
        var returnType = initialize ? typeof(Task) : typeof(Task<IList<Uri>>);
        var legacy = typeof(IGatewayListProvider).GetMethod(legacyName, Type.EmptyTypes);
        Assert.NotNull(legacy);
        Assert.Equal(returnType, legacy.ReturnType);
        Assert.Null(legacy.GetCustomAttribute<ObsoleteAttribute>());
        var current = typeof(IGatewayListProvider).GetMethod(legacyName + "Async", [typeof(CancellationToken)]);
        Assert.NotNull(current);
        Assert.Equal(returnType, current.ReturnType);
        Assert.False(current.IsAbstract);
        var parameter = Assert.Single(current.GetParameters());
        Assert.True(parameter.IsOptional);
        Assert.True(parameter.HasDefaultValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultAsyncMethod_PreservesLegacyResult(bool initialize)
    {
        var provider = new LegacyProvider();
        provider.Completion.SetResult(provider.Gateways);

        var result = await Invoke(provider, initialize, TestContext.Current.CancellationToken);

        Assert.Equal(initialize ? 1 : 0, provider.Initializations);
        Assert.Equal(initialize ? 0 : 1, provider.Lookups);
        Assert.Same(initialize ? null : provider.Gateways, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultAsyncMethod_PreCanceled_DoesNotInvokeLegacyProvider(bool initialize)
    {
        var provider = new LegacyProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Invoke(provider, initialize, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, provider.Initializations);
        Assert.Equal(0, provider.Lookups);
        Assert.False(provider.Completion.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultAsyncMethod_CancelsWaitWithoutCancelingLegacyWork(bool initialize)
    {
        var provider = new LegacyProvider();
        using var cancellation = new CancellationTokenSource();
        var operation = Invoke(provider, initialize, cancellation.Token);
        Assert.Equal(1, provider.Initializations + provider.Lookups);

        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => operation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(operation.IsCanceled);
        Assert.False(provider.Completion.Task.IsCompleted);
        // The compatibility adapter observes this fault even after its caller stops waiting.
        // Do not force global GC/UnobservedTaskException: that would affect unrelated tests.
        provider.Completion.SetException(new InvalidOperationException("Late legacy gateway failure."));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DefaultAsyncMethod_PreservesLegacyFailure(bool initialize, bool synchronous)
    {
        var failure = new InvalidOperationException("Gateway provider failure.");
        var provider = new LegacyProvider { SynchronousFailure = synchronous ? failure : null };
        if (!synchronous)
        {
            provider.Completion.SetException(failure);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Invoke(provider, initialize, TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Equal(1, provider.Initializations + provider.Lookups);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeAsyncMethod_ReceivesTokenInsteadOfLegacyDispatch(bool initialize)
    {
        var provider = new NativeProvider();
        using var cancellation = new CancellationTokenSource();

        var result = await Invoke(provider, initialize, cancellation.Token);

        Assert.Equal(cancellation.Token, provider.ReceivedToken);
        Assert.Equal(initialize, provider.Initialized);
        Assert.Same(initialize ? null : provider.Gateways, result);
        Assert.Equal(0, provider.Initializations + provider.Lookups);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaticProvider_PreCanceled_RejectsOperation(bool initialize)
    {
        var provider = new StaticGatewayListProvider(
            Options.Create(new StaticGatewayListProviderOptions()), Options.Create(new GatewayOptions()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Invoke(provider, initialize, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task StaticProvider_AsyncAndLegacyMethods_ReturnConfiguredGateways()
    {
        var options = new StaticGatewayListProviderOptions
        {
            Gateways = [new Uri("gwy.tcp://127.0.0.1:30001/1"), new Uri("gwy.tcp://127.0.0.1:30002/2")]
        };
        var provider = new StaticGatewayListProvider(Options.Create(options), Options.Create(new GatewayOptions()));

        await provider.InitializeGatewayListProviderAsync(TestContext.Current.CancellationToken);
        Assert.Equal(options.Gateways, await provider.GetGatewaysAsync(TestContext.Current.CancellationToken));
        await provider.InitializeGatewayListProvider();
        Assert.Equal(options.Gateways, await provider.GetGateways());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InProcessProvider_PreCanceled_RejectsOperationWithoutChangingMembership(bool initialize)
    {
        var provider = new InProcessMembershipTable("cluster");
        var initial = await provider.ReadAllAsync(TestContext.Current.CancellationToken);
        var entry = new MembershipEntry
        {
            SiloAddress = SiloAddress.FromParsableString("127.0.0.1:11111@1"),
            Status = SiloStatus.Active,
            ProxyPort = 30001
        };
        Assert.True(await provider.InsertRowAsync(entry, initial.Version.Next(), TestContext.Current.CancellationToken));
        var before = await provider.ReadAllAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Invoke(provider, initialize, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        var after = await provider.ReadAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(Assert.Single(before.Members).Item2, Assert.Single(after.Members).Item2);
        Assert.Equal([new Uri("gwy.tcp://127.0.0.1:30001/1")], await provider.GetGatewaysAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InProcessProvider_AsyncAndLegacyMethods_ReturnOnlyActiveProxyEndpoints()
    {
        var provider = new InProcessMembershipTable("cluster");
        foreach (var (generation, status, proxyPort) in new[]
        {
            (1, SiloStatus.Active, 30001), (2, SiloStatus.Active, 0), (3, SiloStatus.Dead, 30003)
        })
        {
            var before = await provider.ReadAllAsync(TestContext.Current.CancellationToken);
            Assert.True(await provider.InsertRowAsync(new MembershipEntry
            {
                SiloAddress = SiloAddress.FromParsableString($"127.0.0.1:11111@{generation}"),
                Status = status,
                ProxyPort = proxyPort
            }, before.Version.Next(), TestContext.Current.CancellationToken));
        }

        await provider.InitializeGatewayListProviderAsync(TestContext.Current.CancellationToken);
        Assert.Equal([new Uri("gwy.tcp://127.0.0.1:30001/1")], await provider.GetGatewaysAsync(TestContext.Current.CancellationToken));
        await provider.InitializeGatewayListProvider();
        Assert.Equal([new Uri("gwy.tcp://127.0.0.1:30001/1")], await provider.GetGateways());
    }

    private static async Task<IList<Uri>?> Invoke(IGatewayListProvider provider, bool initialize, CancellationToken token = default)
    {
        if (initialize)
        {
            await provider.InitializeGatewayListProviderAsync(token);
            return null;
        }

        return await provider.GetGatewaysAsync(token);
    }

    private class LegacyProvider : IGatewayListProvider
    {
        public IList<Uri> Gateways { get; } = [new Uri("gwy.tcp://127.0.0.1:30000/1")];
        public TaskCompletionSource<IList<Uri>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Initializations { get; private set; }
        public int Lookups { get; private set; }
        public Exception? SynchronousFailure { get; init; }
        public TimeSpan MaxStaleness => TimeSpan.FromMinutes(1);
        public bool IsUpdatable => true;

        public Task InitializeGatewayListProvider()
        {
            Initializations++;
            if (SynchronousFailure is { } failure)
            {
                throw failure;
            }

            return Completion.Task;
        }

        public Task<IList<Uri>> GetGateways()
        {
            Lookups++;
            if (SynchronousFailure is { } failure)
            {
                throw failure;
            }

            return Completion.Task;
        }
    }

    private sealed class NativeProvider : LegacyProvider, IGatewayListProvider
    {
        public CancellationToken ReceivedToken { get; private set; }
        public bool Initialized { get; private set; }

        public Task InitializeGatewayListProviderAsync(CancellationToken cancellationToken = default)
        {
            ReceivedToken = cancellationToken;
            Initialized = true;
            return Task.CompletedTask;
        }

        public Task<IList<Uri>> GetGatewaysAsync(CancellationToken cancellationToken = default)
        {
            ReceivedToken = cancellationToken;
            return Task.FromResult(Gateways);
        }
    }
}
