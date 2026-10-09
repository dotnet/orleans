using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.TestingHost;
using ReleasedPeer;
using ReleasedPeer.Contracts;
using Xunit;

namespace Orleans.ReleasedPeer.Tests;

[Trait("Suite", "Functional")]
[Trait("Provider", "None")]
[Trait("Area", "Runtime")]
[Trait("Category", "Functional")]
public sealed class ReleasedPeerRetirementTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private const int Argument = 173;
    private const int ExpectedResult = 526;

    [Theory]
    [InlineData("10.3.1")]
    [InlineData("10.4.0")]
    public async Task NewCaller_OldSilo_CompletesOriginalRequestOnce(string version)
    {
        using var ports = new TestClusterPortAllocator();
        var (silo, gateway) = ports.AllocateConsecutivePortPairs(2);
        var cluster = Guid.NewGuid().ToString("N");
        await using var current = await CurrentSilo.StartAsync(silo, gateway, silo, cluster);
        await using var old = await ReleasedPeerProcess.StartAsync(version, silo + 1, gateway + 1, silo, cluster);
        VerifyReleasedIdentity(old, version);

        var grainId = Guid.NewGuid();
        Assert.Equal(old.Silo, await old.CommandAsync<string>("prime", grainId));
        var operation = Guid.NewGuid();
        var context = $"new-to-old:{operation:N}";
        var requestWait = old.RequestAsync(context);
        var invocation = Invoke(current.Client, grainId, operation, context);
        var request = await requestWait;
        var result = await invocation.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        VerifyResult(result, operation, context, old.Silo);
        VerifyRequest(request, context);
        Assert.Equal(0, request.ForwardCount);
        Assert.Equal(old.Silo, Parsable(request.TargetSilo));
        Assert.Equal(current.Address, Parsable(request.SendingSilo));
        Assert.Empty(current.Control.Entries);
        Assert.Equal(result, Assert.Single(await old.CommandAsync<InvocationResult[]>("entries")));
    }

    [Theory]
    [InlineData("10.3.1")]
    [InlineData("10.4.0")]
    public async Task OldCaller_NewSilo_CompletesOriginalRequestOnce(string version)
    {
        using var ports = new TestClusterPortAllocator();
        var (silo, gateway) = ports.AllocateConsecutivePortPairs(2);
        var cluster = Guid.NewGuid().ToString("N");
        await using var current = await CurrentSilo.StartAsync(silo, gateway, silo, cluster);
        var grainId = Guid.NewGuid();
        Assert.Equal(current.Address, await current.Client.GetGrain<IRetirementProbeGrain>(grainId).Locate());
        await using var old = await ReleasedPeerProcess.StartAsync(version, silo + 1, gateway + 1, silo, cluster);
        VerifyReleasedIdentity(old, version);

        var operation = Guid.NewGuid();
        var context = $"old-to-new:{operation:N}";
        var requestWait = current.Probe.WaitForRequest(context);
        var invocation = old.CommandAsync<InvocationResult>("invoke", grainId, operation, Argument, context);
        var request = await requestWait.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var result = await invocation;
        VerifyResult(result, operation, context, current.Address);
        VerifyRequest(request, context);
        Assert.Equal(0, request.ForwardCount);
        Assert.Equal(old.Silo, Parsable(request.SendingSilo));
        Assert.Equal(result, Assert.Single(current.Control.Entries));
        Assert.Empty(await old.CommandAsync<InvocationResult[]>("entries"));
    }

    [Theory]
    [InlineData("10.3.1")]
    [InlineData("10.4.0")]
    public async Task OldClient_ThroughStableGateway_NewRetiringSilo_ReceiverForwardsOriginalRequest(string version)
    {
        using var ports = new TestClusterPortAllocator();
        var (silo, gateway) = ports.AllocateConsecutivePortPairs(3);
        var cluster = Guid.NewGuid().ToString("N");
        await using var replacement = await CurrentSilo.StartAsync(silo, gateway, silo, cluster);
        await using var retiring = await CurrentSilo.StartAsync(silo + 1, gateway + 1, silo, cluster);
        var grainId = Guid.NewGuid();
        Assert.Equal(retiring.Address, await LocateOn(retiring.Client, grainId, retiring.Address));
        retiring.Control.HoldDeactivation = true;
        // A released hosted caller still has its original premature membership
        // sweep. The released external client keeps its physical route at the
        // stable gateway, so this qualifies receiver-owned forwarding without pretending
        // that updating the receiver can repair a released caller's runtime.
        await using var old = await ReleasedPeerProcess.StartAsync(version, 0, gateway, silo, cluster, clientOnly: true);
        VerifyReleasedIdentity(old, version);
        Assert.Equal(retiring.Address, await old.CommandAsync<string>("prime", grainId));

        var blocker = retiring.Client.GetGrain<IRetirementProbeGrain>(grainId).Block();
        await retiring.Control.RunningEntered.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var operation = Guid.NewGuid();
        var context = $"legacy-queued:{operation:N}";
        var firstWait = retiring.Probe.WaitForRequest(context);
        var finalWait = replacement.Probe.WaitForRequest(context);
        var invocation = old.CommandAsync<InvocationResult>(
            "invoke", grainId, operation, Argument, context, replacement.Address);
        var first = await firstWait.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        VerifyRequest(first, context);
        Assert.Equal(0, first.ForwardCount);
        Assert.Equal(retiring.Address, Parsable(first.TargetSilo));
        Assert.Equal(replacement.Address, Parsable(first.SendingSilo));
        Assert.StartsWith("sys.client/", first.SendingGrain);
        Assert.Empty(retiring.Control.Entries);
        Assert.Empty(replacement.Control.Entries);
        Assert.False(invocation.IsCompleted);

        // Queue before membership retirement can invalidate the stable gateway's
        // cached address, then freeze admission before releasing the running call.
        retiring.Control.GrainContext!.Deactivate(
            new(DeactivationReasonCode.ShuttingDown, "Released-client graceful retirement barrier."),
            TestContext.Current.CancellationToken);
        var stopping = retiring.Host.StopAsync(TestContext.Current.CancellationToken);
        retiring.Control.ReleaseRunning.TrySetResult();
        await blocker.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await retiring.Control.DeactivationEntered.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        retiring.Control.ReleaseDeactivation.TrySetResult();
        var final = await finalWait.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        VerifySameInvocation(first, final);
        Assert.Equal(1, final.ForwardCount);
        var result = await invocation;
        VerifyResult(result, operation, context, replacement.Address);
        await stopping.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(result, Assert.Single(replacement.Control.Entries));
        Assert.Empty(retiring.Control.Entries);
        Assert.Empty(await old.CommandAsync<InvocationResult[]>("entries"));
    }

    [Theory]
    [InlineData("10.3.1")]
    [InlineData("10.4.0")]
    public async Task NewCaller_OldRetiringSilo_NewReplacement_PreservesOriginalInvocationWithoutRouteHints(string version)
    {
        using var ports = new TestClusterPortAllocator();
        var (silo, gateway) = ports.AllocateConsecutivePortPairs(2);
        var cluster = Guid.NewGuid().ToString("N");
        await using var current = await CurrentSilo.StartAsync(silo, gateway, silo, cluster);
        await using var old = await ReleasedPeerProcess.StartAsync(version, silo + 1, gateway + 1, silo, cluster);
        VerifyReleasedIdentity(old, version);
        var grainId = Guid.NewGuid();
        Assert.Equal(old.Silo, await old.CommandAsync<string>("prime", grainId));
        // Populate the current caller's cached physical address before A retires.
        Assert.Equal(old.Silo, await current.Client.GetGrain<IRetirementProbeGrain>(grainId).Locate());
        await old.CommandAsync<bool>("hold-deactivation");
        var blocker = old.CommandAsync<bool>("block", grainId);
        await old.Running;
        current.Control.HoldActivation = true;

        var operation = Guid.NewGuid();
        var context = $"new-old-new:{operation:N}";
        var firstWait = old.RequestAsync(context);
        var finalWait = current.Probe.WaitForRequest(context);
        var invocation = Invoke(current.Client, grainId, operation, context, current.Address);
        var first = await firstWait;
        VerifyRequest(first, context);
        Assert.Equal(0, first.ForwardCount);
        Assert.Equal(old.Silo, Parsable(first.TargetSilo));
        Assert.Empty(await old.CommandAsync<InvocationResult[]>("entries"));
        Assert.Empty(current.Control.Entries);
        Assert.False(invocation.IsCompleted);

        // Queue while the released silo still accepts requests: its legacy
        // shutdown transport starts rejecting late arrivals before deactivation.
        var stopping = old.CommandAsync<bool>("stop");
        await old.CommandAsync<bool>("release-running");
        Assert.True(await blocker);
        await old.Deactivating;
        await old.CommandAsync<bool>("release-deactivation");
        await current.Control.ActivationEntered.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var final = await finalWait.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        VerifySameInvocation(first, final);
        Assert.Equal(1, final.ForwardCount);
        // The released receiver has really completed graceful shutdown while
        // the new callback remains pending below application admission.
        Assert.True(await stopping);
        Assert.False(invocation.IsCompleted);
        Assert.Empty(current.Control.Entries);
        current.Control.ReleaseActivation.TrySetResult();
        var result = await invocation.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        VerifyResult(result, operation, context, current.Address);
        Assert.Equal(result, Assert.Single(current.Control.Entries));
        Assert.Empty(await old.CommandAsync<InvocationResult[]>("entries"));
    }

    private static Task<InvocationResult> Invoke(
        IClusterClient client, Guid grain, Guid operation, string context, string target = "")
    {
        RequestContext.Set(ProbeControl.ContextKey, context);
        if (target.Length > 0)
        {
            RequestContext.Set("PlacementHintKey", SiloAddress.FromParsableString(target));
        }
        try
        {
            return client.GetGrain<IRetirementProbeGrain>(grain).Execute(operation, Argument);
        }
        finally
        {
            RequestContext.Clear();
        }
    }

    private static Task<string> LocateOn(IClusterClient client, Guid grain, string target)
    {
        RequestContext.Set("PlacementHintKey", SiloAddress.FromParsableString(target));
        try
        {
            return client.GetGrain<IRetirementProbeGrain>(grain).Locate();
        }
        finally
        {
            RequestContext.Clear();
        }
    }

    private static void VerifyReleasedIdentity(ReleasedPeerProcess peer, string version)
    {
        Assert.StartsWith(version, peer.Identity.GetProperty("Version").GetString());
        Assert.StartsWith(version, peer.Identity.GetProperty("CoreVersion").GetString());
        Assert.NotEqual(
            Assembly.Load("Orleans.Runtime").ManifestModule.ModuleVersionId,
            peer.Identity.GetProperty("Mvid").GetGuid());
        Assert.Contains(Path.Combine("released", version), peer.Identity.GetProperty("Path").GetString());
        Assert.Contains(Path.Combine("released", version), peer.Identity.GetProperty("CorePath").GetString());
        Assert.NotEqual(
            Assembly.Load("Orleans.Core").ManifestModule.ModuleVersionId,
            peer.Identity.GetProperty("CoreMvid").GetGuid());
    }

    private static void VerifyResult(InvocationResult result, Guid operation, string context, string silo)
    {
        Assert.Equal(operation, result.OperationId);
        Assert.Equal(ExpectedResult, result.Value);
        Assert.Equal(context, result.Context);
        Assert.Equal(silo, result.Silo);
        Assert.Equal(1, result.EntryCount);
    }

    private static void VerifyRequest(MessageSnapshot request, string context)
    {
        Assert.Equal("Request", request.Direction);
        Assert.Equal("admitted", request.Stage);
        Assert.Equal(context, request.Context);
        Assert.NotEmpty(request.Id);
        Assert.NotEmpty(request.SendingGrain);
        Assert.NotEmpty(request.TargetGrain);
    }

    private static void VerifySameInvocation(MessageSnapshot first, MessageSnapshot final)
    {
        Assert.Equal(first.Id, final.Id);
        Assert.Equal(first.SendingSilo, final.SendingSilo);
        Assert.Equal(first.SendingGrain, final.SendingGrain);
        Assert.Equal(first.TargetGrain, final.TargetGrain);
        Assert.Equal(first.Context, final.Context);
        Assert.NotEqual(first.TargetSilo, final.TargetSilo);
        Assert.Equal("admitted", final.Stage);
    }

    private static string Parsable(string display)
    {
        // Message snapshots use SiloAddress.ToString, whose display form includes S.
        var value = display.StartsWith("S", StringComparison.Ordinal) ? display[1..] : display;
        return value;
    }

    private sealed class CurrentSilo : IAsyncDisposable
    {
        public IHost Host { get; }
        public ProbeControl Control { get; } = new();
        public RuntimeProbe Probe { get; } = new();
        public IClusterClient Client => Host.Services.GetRequiredService<IClusterClient>();
        public string Address => Host.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress.ToParsableString();

        private CurrentSilo(int silo, int gateway, int primary, string cluster)
        {
            Host = new HostBuilder()
                .ConfigureLogging(logging => logging.ClearProviders())
                .ConfigureServices(services => services.AddSingleton(Control))
                .UseOrleans(builder => builder
                    .UseLocalhostClustering(silo, gateway, new IPEndPoint(IPAddress.Loopback, primary), clusterId: cluster, serviceId: cluster)
                    .Configure<SiloMessagingOptions>(options => options.ResponseTimeout = TimeSpan.FromSeconds(30))
                    .Configure<ClusterMembershipOptions>(options => options.TableRefreshTimeout = TimeSpan.FromMilliseconds(200)))
                .Build();
        }

        public static async Task<CurrentSilo> StartAsync(int silo, int gateway, int primary, string cluster)
        {
            var result = new CurrentSilo(silo, gateway, primary, cluster);
            try
            {
                await result.Host.StartAsync().WaitAsync(Timeout);
                result.Probe.Install(result.Host.Services);
                return result;
            }
            catch
            {
                await result.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Control.ReleaseDeactivation.TrySetResult();
            Control.ReleaseActivation.TrySetResult();
            Control.ReleaseRunning.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await Host.StopAsync(timeout.Token);
            }
            finally
            {
                Host.Dispose();
            }
        }
    }
}
