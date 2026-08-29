using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.Messaging;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT"), TestCategory("Messaging")]
public sealed class SharedEntryMessageTargetFastPathTests : IClassFixture<SharedEntryMessageTargetFastPathTests.Fixture>
{
    private static long _nextGrainKey = 10_000_000;
    private readonly Fixture _fixture;

    public SharedEntryMessageTargetFastPathTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task LocalDirectoryGrain_AfterTwoCalls_BindsEntryToExactActivation()
    {
        var primary = (InProcessSiloHandle)_fixture.HostedCluster.Primary!;
        var grainFactory = GetPrimarySiloGrainFactory(primary);
        var grain = grainFactory.GetGrain<ITestGrain>(Interlocked.Increment(ref _nextGrainKey));
        var grainReference = Assert.IsAssignableFrom<GrainReference>(grain);
        const string label = "local-fast-path";

        RequestContext.Set(IPlacementDirector.PlacementHintKey, primary.SiloAddress);
        try
        {
            await grain.SetLabel(label);
            Assert.Equal(label, await grain.GetLabel());
        }
        finally
        {
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }

        var entry = GetEntry(grainReference);
        Assert.True(entry.IsValid);
        Assert.Equal(grainReference.GrainId, entry.Address.GrainId);
        Assert.Equal(primary.SiloAddress, entry.Address.SiloAddress);
        Assert.True(_fixture.HostedCluster.TryGetGrainContext(grainReference.GrainId, out var grainContext));
        var activation = Assert.IsType<ActivationData>(grainContext);
        Assert.Equal(activation.Address, entry.Address);
        Assert.True(entry.TryGetMessageTarget(out var messageTarget));
        Assert.Same(activation, messageTarget);
    }

    [Fact]
    public async Task InvalidateCache_DisposesRetainedEntry_AndNextCallCapturesDifferentEntry()
    {
        var primary = (InProcessSiloHandle)_fixture.HostedCluster.Primary!;
        var grainFactory = GetPrimarySiloGrainFactory(primary);
        var grain = grainFactory.GetGrain<ITestGrain>(Interlocked.Increment(ref _nextGrainKey));
        var grainReference = Assert.IsAssignableFrom<GrainReference>(grain);
        const string label = "entry-before-invalidation";

        RequestContext.Set(IPlacementDirector.PlacementHintKey, primary.SiloAddress);
        try
        {
            await grain.SetLabel(label);
            Assert.Equal(label, await grain.GetLabel());
        }
        finally
        {
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }

        var retainedEntry = GetEntry(grainReference);
        Assert.True(retainedEntry.IsValid);
        primary.ServiceProvider.GetRequiredService<GrainLocator>().InvalidateCache(grainReference.GrainId);
        Assert.False(retainedEntry.IsValid);
        Assert.False(retainedEntry.TryGetMessageTarget(out var disposedTarget));
        Assert.Null(disposedTarget);
        Assert.Same(retainedEntry.ReferenceHandle, grainReference.MessageTargetCache);

        Assert.Equal(label, await grain.GetLabel());

        var replacementEntry = GetEntry(grainReference);
        Assert.NotSame(retainedEntry, replacementEntry);
        Assert.True(replacementEntry.IsValid);
        Assert.Equal(grainReference.GrainId, replacementEntry.Address.GrainId);
        Assert.Equal(primary.SiloAddress, replacementEntry.Address.SiloAddress);
        Assert.True(_fixture.HostedCluster.TryGetGrainContext(grainReference.GrainId, out var grainContext));
        var activation = Assert.IsType<ActivationData>(grainContext);
        Assert.True(replacementEntry.TryGetMessageTarget(out var replacementTarget));
        Assert.Same(activation, replacementTarget);
    }

    [Fact]
    public async Task RemoteDirectoryGrain_DoesNotBindConnectionTarget()
    {
        var primary = (InProcessSiloHandle)_fixture.HostedCluster.Primary!;
        var secondary = _fixture.HostedCluster.Silos.Single(
            silo => !silo.SiloAddress.Equals(primary.SiloAddress));
        var grainFactory = GetPrimarySiloGrainFactory(primary);
        var grain = grainFactory.GetGrain<ITestGrain>(Interlocked.Increment(ref _nextGrainKey));
        var grainReference = Assert.IsAssignableFrom<GrainReference>(grain);
        const string label = "remote-fast-path";

        RequestContext.Set(IPlacementDirector.PlacementHintKey, secondary.SiloAddress);
        try
        {
            await grain.SetLabel(label);
            Assert.Equal(label, await grain.GetLabel());
        }
        finally
        {
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }

        Assert.Null(grainReference.MessageTargetCache);
    }

    [Fact]
    public async Task LocalDirectoryGrain_DeactivationDoesNotReuseInvalidActivation()
    {
        var primary = (InProcessSiloHandle)_fixture.HostedCluster.Primary!;
        var grainFactory = GetPrimarySiloGrainFactory(primary);
        var grain = grainFactory.GetGrain<IOneWayGrain>(Guid.NewGuid());
        var grainReference = Assert.IsAssignableFrom<GrainReference>(grain);

        RequestContext.Set(IPlacementDirector.PlacementHintKey, primary.SiloAddress);
        try
        {
            _ = await grain.GetActivationId();
            _ = await grain.GetActivationId();
        }
        finally
        {
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }

        var originalEntry = GetEntry(grainReference);
        Assert.True(originalEntry.TryGetMessageTarget(out var originalTarget));
        var originalActivation = Assert.IsType<ActivationData>(originalTarget);
        var originalActivationId = await grain.GetActivationId();

        await grain.Deactivate();
        RequestContext.Set(IPlacementDirector.PlacementHintKey, primary.SiloAddress);
        try
        {
            var reboundActivationId = await grain.GetActivationId();
            Assert.Equal(reboundActivationId, await grain.GetActivationId());

            Assert.NotEqual(originalActivationId, reboundActivationId);
            Assert.False(originalActivation.IsValid);
            Assert.True(_fixture.HostedCluster.TryGetGrainContext(grainReference.GrainId, out var reboundContext));
            var reboundActivation = Assert.IsType<ActivationData>(reboundContext);
            Assert.NotSame(originalActivation, reboundActivation);
            Assert.True(reboundActivation.IsValid);
            Assert.Equal(primary.SiloAddress, reboundActivation.Address.SiloAddress);
        }
        finally
        {
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }
    }

    [Fact]
    public async Task CompatibleInterfaceCast_TransfersSameEntryHandle()
    {
        var primary = (InProcessSiloHandle)_fixture.HostedCluster.Primary!;
        var grainFactory = GetPrimarySiloGrainFactory(primary);
        var writer = grainFactory.GetGrain<IMultifacetWriter>(Interlocked.Increment(ref _nextGrainKey));
        var writerReference = Assert.IsAssignableFrom<GrainReference>(writer);
        const int value = 1729;

        RequestContext.Set(IPlacementDirector.PlacementHintKey, primary.SiloAddress);
        try
        {
            await writer.SetValue(-1);
            await writer.SetValue(value);
        }
        finally
        {
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }

        var writerEntry = GetEntry(writerReference);
        Assert.True(writerEntry.IsValid);

        var reader = writer.AsReference<IMultifacetReader>();
        var readerReference = Assert.IsAssignableFrom<GrainReference>(reader);

        Assert.NotSame(writerReference, readerReference);
        Assert.Same(writerEntry.ReferenceHandle, readerReference.MessageTargetCache);
        Assert.Equal(writerReference.GrainId, readerReference.GrainId);
        Assert.Equal(value, await reader.GetValue());
        Assert.Same(writerEntry.ReferenceHandle, readerReference.MessageTargetCache);
    }

    [Fact]
    public async Task ExternalClientCalls_DoNotAttachMessageTargetCache()
    {
        var grain = _fixture.Client.GetGrain<ITestGrain>(Interlocked.Increment(ref _nextGrainKey));

        await grain.SetLabel("external-client");

        Assert.Null(Assert.IsAssignableFrom<GrainReference>(grain).MessageTargetCache);
    }

    [Fact]
    public async Task StatelessWorkerCalls_DoNotAttachDirectoryEntries()
    {
        var primary = (InProcessSiloHandle)_fixture.HostedCluster.Primary!;
        var grainFactory = GetPrimarySiloGrainFactory(primary);
        var grain = grainFactory.GetGrain<IStatelessWorkerActivationCollectorTestGrain1>(Guid.NewGuid());

        await grain.Nop();

        Assert.Null(Assert.IsAssignableFrom<GrainReference>(grain).MessageTargetCache);
    }

    [Fact]
    public async Task CacheInvalidationHeader_BypassesFastPathWithoutDiscardingLiveHandle()
    {
        var primary = (InProcessSiloHandle)_fixture.HostedCluster.Primary!;
        var grainFactory = GetPrimarySiloGrainFactory(primary);
        var grain = grainFactory.GetGrain<ITestGrain>(Interlocked.Increment(ref _nextGrainKey));
        var grainReference = Assert.IsAssignableFrom<GrainReference>(grain);
        RequestContext.Set(IPlacementDirector.PlacementHintKey, primary.SiloAddress);
        try
        {
            await grain.SetLabel("cache-header");
            Assert.Equal("cache-header", await grain.GetLabel());
        }
        finally
        {
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }

        var retainedEntry = GetEntry(grainReference);
        var unrelatedAddress = new GrainAddress
        {
            GrainId = GrainId.Create("unrelated", Guid.NewGuid().ToString()),
            SiloAddress = primary.SiloAddress,
        };
        var message = new Message
        {
            Direction = Message.Directions.Request,
            TargetGrain = grainReference.GrainId,
            CacheInvalidationHeader = [new GrainAddressCacheUpdate(unrelatedAddress, validAddress: null)],
        };

        var messageCenter = primary.ServiceProvider.GetRequiredService<MessageCenter>();
        Assert.False(messageCenter.TryGetDirectoryCacheEntry(grainReference, message, out var selectedEntry));

        Assert.Null(selectedEntry);
        Assert.True(retainedEntry.IsValid);
        Assert.Same(retainedEntry.ReferenceHandle, grainReference.MessageTargetCache);
    }

    [Fact]
    public void RemoteSiloEntry_IsRejectedAndClearedFromGrainReference()
    {
        var primary = (InProcessSiloHandle)_fixture.HostedCluster.Primary!;
        var grainFactory = GetPrimarySiloGrainFactory(primary);
        var grain = grainFactory.GetGrain<ITestGrain>(Interlocked.Increment(ref _nextGrainKey));
        var grainReference = Assert.IsAssignableFrom<GrainReference>(grain);
        var address = new GrainAddress
        {
            GrainId = grainReference.GrainId,
            ActivationId = ActivationId.NewId(),
            SiloAddress = SiloAddress.FromParsableString("127.0.0.1:54321@1"),
            MembershipVersion = new MembershipVersion(0),
        };
        var entry = new GrainDirectoryCacheEntry(address, version: 0);
        grainReference.MessageTargetCache = entry.ReferenceHandle;
        var message = new Message
        {
            Direction = Message.Directions.Request,
            TargetGrain = grainReference.GrainId,
        };

        var messageCenter = primary.ServiceProvider.GetRequiredService<MessageCenter>();
        Assert.False(messageCenter.TryGetDirectoryCacheEntry(grainReference, message, out var selectedEntry));

        Assert.Null(selectedEntry);
        Assert.Null(grainReference.MessageTargetCache);
        Assert.True(entry.IsValid);
    }

    private static IGrainFactory GetPrimarySiloGrainFactory(InProcessSiloHandle primary)
    {
        var runtimeClient = primary.ServiceProvider.GetRequiredService<InsideRuntimeClient>();
        var grainFactory = primary.ServiceProvider.GetRequiredService<IGrainFactory>();
        Assert.Same(runtimeClient.ConcreteGrainFactory, grainFactory);
        return grainFactory;
    }

    private static GrainDirectoryCacheEntry GetEntry(GrainReference grainReference)
    {
        var handle = Assert.IsType<WeakReference<GrainDirectoryCacheEntry>>(grainReference.MessageTargetCache);
        Assert.True(handle.TryGetTarget(out var entry));
        return entry;
    }

    public sealed class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 2;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("MemoryStore");
        }
    }
}
