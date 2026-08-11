using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.GrainDirectory.EntityFrameworkCore.Data;
using Orleans.Runtime;
using Tester.Directories;
using Xunit.Abstractions;

namespace Orleans.EntityFrameworkCore.Tests.GrainDirectory;

public abstract class EFCoreGrainDirectoryTestsBase<TDbContext, TETag> :
    GrainDirectoryTests<EFCoreGrainDirectory<TDbContext, TETag>>,
    IAsyncLifetime
    where TDbContext : GrainDirectoryDbContext<TDbContext, TETag>
{
    private readonly ITestOutputHelper _testOutput;
    private readonly string _clusterId = $"directory-{Guid.NewGuid():N}";
    private EFCoreDatabaseFixture<TDbContext>? _fixture;
    private EFCoreGrainDirectory<TDbContext, TETag>? _directory;

    protected EFCoreGrainDirectoryTestsBase(ITestOutputHelper testOutput)
        : base(testOutput)
    {
        _testOutput = testOutput;
    }

    protected abstract EFCoreTestDatabase Database { get; }

    protected abstract IEFGrainDirectoryETagConverter<TETag> CreateETagConverter();

    protected override EFCoreGrainDirectory<TDbContext, TETag> CreateGrainDirectory() =>
        _directory ?? throw new InvalidOperationException("The grain directory has not been initialized.");

    public async Task InitializeAsync()
    {
        _fixture = new EFCoreDatabaseFixture<TDbContext>(
            Database,
            "directory",
            $"{GetType().Name}_{GetTargetFramework()}",
            writeOutput: message => _testOutput.WriteLine(message));
        await _fixture.InitializeAsync();
        _directory = CreateDirectory(_clusterId);
    }

    public async Task DisposeAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task UnregisterMany_RemovesOnlyMatchingActivations()
    {
        const int count = 25;
        const int mismatchIndex = 4;
        var addresses = Enumerable.Range(0, count)
            .Select(index => CreateAddress(index, index % 3))
            .ToList();

        foreach (var address in addresses)
        {
            Assert.Equal(address, await GrainDirectory.Register(address));
        }

        var registeredMismatch = addresses[mismatchIndex];
        addresses[mismatchIndex] = CopyAddress(
            registeredMismatch,
            activationId: ActivationId.NewId());

        await GrainDirectory.UnregisterMany(addresses);

        for (var index = 0; index < addresses.Count; index++)
        {
            var result = await GrainDirectory.Lookup(addresses[index].GrainId);
            if (index == mismatchIndex)
            {
                Assert.Equal(registeredMismatch, result);
            }
            else
            {
                Assert.Null(result);
            }
        }

        await using var context = await Factory.CreateDbContextAsync();
        var retained = await context.Activations
            .AsNoTracking()
            .Where(record => record.ClusterId == _clusterId)
            .ToListAsync();
        var retainedRecord = Assert.Single(retained);
        Assert.Equal(registeredMismatch.GrainId.ToString(), retainedRecord.GrainId);
        Assert.Equal(registeredMismatch.ActivationId.ToParsableString(), retainedRecord.ActivationId);
    }

    [SkippableFact]
    public async Task UnregisterSilos_RemovesOnlyRequestedSilosInCurrentCluster()
    {
        var addresses = new[]
        {
            CreateAddress(100, 10),
            CreateAddress(101, 11),
            CreateAddress(102, 10),
            CreateAddress(103, 12)
        };

        foreach (var address in addresses)
        {
            Assert.Equal(address, await GrainDirectory.Register(address));
        }

        await GrainDirectory.UnregisterSilos(
            [addresses[0].SiloAddress!, addresses[3].SiloAddress!]);

        Assert.Null(await GrainDirectory.Lookup(addresses[0].GrainId));
        Assert.Equal(addresses[1], await GrainDirectory.Lookup(addresses[1].GrainId));
        Assert.Null(await GrainDirectory.Lookup(addresses[2].GrainId));
        Assert.Null(await GrainDirectory.Lookup(addresses[3].GrainId));

        await using var context = await Factory.CreateDbContextAsync();
        var retained = await context.Activations
            .AsNoTracking()
            .Where(record => record.ClusterId == _clusterId)
            .ToListAsync();
        var retainedRecord = Assert.Single(retained);
        Assert.Equal(addresses[1].GrainId.ToString(), retainedRecord.GrainId);
        Assert.Equal(addresses[1].SiloAddress!.ToParsableString(), retainedRecord.SiloAddress);
    }

    [SkippableFact]
    public async Task UnregisterSilos_DoesNotCrossClusterBoundary()
    {
        var secondaryClusterId = $"secondary-{Guid.NewGuid():N}";
        var secondaryDirectory = CreateDirectory(secondaryClusterId);
        var primaryAddress = CreateAddress(200, 20);
        var secondaryAddress = CopyAddress(primaryAddress, activationId: ActivationId.NewId());

        Assert.Equal(primaryAddress, await GrainDirectory.Register(primaryAddress));
        Assert.Equal(secondaryAddress, await secondaryDirectory.Register(secondaryAddress));

        await GrainDirectory.UnregisterSilos([primaryAddress.SiloAddress!]);

        Assert.Null(await GrainDirectory.Lookup(primaryAddress.GrainId));
        Assert.Equal(secondaryAddress, await secondaryDirectory.Lookup(secondaryAddress.GrainId));

        await using var context = await Factory.CreateDbContextAsync();
        var retained = await context.Activations.AsNoTracking().ToListAsync();
        var retainedRecord = Assert.Single(retained);
        Assert.Equal(secondaryClusterId, retainedRecord.ClusterId);
        Assert.Equal(secondaryAddress.ActivationId.ToParsableString(), retainedRecord.ActivationId);
    }

    [SkippableFact]
    public async Task Register_StaleETagUpdateFailsAndPreservesWinner()
    {
        var address = CreateAddress(300, 30);
        Assert.Equal(address, await GrainDirectory.Register(address));

        await using var winner = await Factory.CreateDbContextAsync();
        await using var stale = await Factory.CreateDbContextAsync();
        var winnerRecord = await FindRecord(winner, address.GrainId);
        var staleRecord = await FindRecord(stale, address.GrainId);
        var insertedETag = CreateETagConverter().FromDbETag(winnerRecord.ETag);
        Assert.Equal(insertedETag, CreateETagConverter().FromDbETag(staleRecord.ETag));

        winnerRecord.MembershipVersion = 301;
        Assert.Equal(1, await winner.SaveChangesAsync());
        var winnerETag = CreateETagConverter().FromDbETag(winnerRecord.ETag);
        Assert.NotEqual(insertedETag, winnerETag);

        staleRecord.MembershipVersion = 302;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());

        await using var verification = await Factory.CreateDbContextAsync();
        var persisted = await FindRecord(verification, address.GrainId);
        Assert.Equal(301, persisted.MembershipVersion);
        Assert.Equal(winnerETag, CreateETagConverter().FromDbETag(persisted.ETag));

        var lookup = await GrainDirectory.Lookup(address.GrainId);
        Assert.NotNull(lookup);
        Assert.Equal(new MembershipVersion(301), lookup.MembershipVersion);
        Assert.Equal(address.ActivationId, lookup.ActivationId);
    }

    [SkippableFact]
    public async Task Participate_SubscribesAtRuntimeInitialize()
    {
        var lifecycle = new RecordingSiloLifecycle();

        GrainDirectory.Participate(lifecycle);

        Assert.Equal("EFCoreGrainDirectory", lifecycle.ObserverName);
        Assert.Equal(ServiceLifecycleStage.RuntimeInitialize, lifecycle.Stage);
        Assert.NotNull(lifecycle.Observer);

        await lifecycle.StartAsync(CancellationToken.None);
        Assert.True(lifecycle.Started);
    }

    [SkippableFact]
    public void ToGrainAddress_MapsAllAddressFields()
    {
        var expected = CreateAddress(400, 40);
        var record = new GrainActivationRecord<TETag>
        {
            ClusterId = _clusterId,
            GrainId = expected.GrainId.ToString(),
            SiloAddress = expected.SiloAddress!.ToParsableString(),
            ActivationId = expected.ActivationId.ToParsableString(),
            MembershipVersion = expected.MembershipVersion.Value
        };

        var result = GrainDirectory.ToGrainAddress(record);

        Assert.Equal(expected.GrainId, result.GrainId);
        Assert.Equal(expected.SiloAddress, result.SiloAddress);
        Assert.Equal(expected.ActivationId, result.ActivationId);
        Assert.Equal(expected.MembershipVersion, result.MembershipVersion);
    }

    private IDbContextFactory<TDbContext> Factory =>
        _fixture?.Factory ?? throw new InvalidOperationException("The database fixture has not been initialized.");

    private EFCoreGrainDirectory<TDbContext, TETag> CreateDirectory(string clusterId) =>
        new(
            loggerFactory,
            Factory,
            Options.Create(new ClusterOptions
            {
                ClusterId = clusterId,
                ServiceId = $"service-{Guid.NewGuid():N}"
            }),
            CreateETagConverter());

    private async Task<GrainActivationRecord<TETag>> FindRecord(
        TDbContext context,
        GrainId grainId) =>
        await context.Activations.SingleAsync(
            record => record.ClusterId == _clusterId && record.GrainId == grainId.ToString());

    private static GrainAddress CreateAddress(int grainIndex, int siloIndex) =>
        new()
        {
            ActivationId = ActivationId.NewId(),
            GrainId = GrainId.Parse($"user/efcore-directory-{grainIndex}-{Guid.NewGuid():N}"),
            SiloAddress = SiloAddress.FromParsableString(
                $"10.0.{siloIndex / 250}.{(siloIndex % 250) + 1}:{1000 + siloIndex}@{5000 + siloIndex}"),
            MembershipVersion = new MembershipVersion(51 + grainIndex)
        };

    private static GrainAddress CopyAddress(
        GrainAddress source,
        ActivationId activationId) =>
        new()
        {
            ActivationId = activationId,
            GrainId = source.GrainId,
            SiloAddress = source.SiloAddress,
            MembershipVersion = source.MembershipVersion
        };

    private static string GetTargetFramework()
    {
#if NET8_0
        return "net8";
#elif NET10_0
        return "net10";
#else
        return "unknown";
#endif
    }

    private sealed class RecordingSiloLifecycle : ISiloLifecycle
    {
        public string? ObserverName { get; private set; }

        public int? Stage { get; private set; }

        public ILifecycleObserver? Observer { get; private set; }

        public bool Started { get; private set; }

        public int HighestCompletedStage => Started ? Stage ?? 0 : 0;

        public int LowestStoppedStage => 0;

        public IDisposable Subscribe(string observerName, int stage, ILifecycleObserver observer)
        {
            Assert.Null(Observer);
            ObserverName = observerName;
            Stage = stage;
            Observer = observer;
            return NoopDisposable.Instance;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await Assert.IsAssignableFrom<ILifecycleObserver>(Observer).OnStart(cancellationToken);
            Started = true;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
