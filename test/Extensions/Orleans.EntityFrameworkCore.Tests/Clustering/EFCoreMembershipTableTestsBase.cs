using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Clustering.EntityFrameworkCore.Data;
using Orleans.Configuration;
using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.Messaging;
using Orleans.Runtime;
using TestExtensions;
using UnitTests;
using UnitTests.MembershipTests;

namespace Orleans.EntityFrameworkCore.Tests.Clustering;

public abstract class EFCoreMembershipTableTestsBase<TDbContext, TETag> :
    MembershipTableTestsBase,
    IDisposable
    where TDbContext : ClusterDbContext<TDbContext, TETag>
{
    private static int _generation;
    private ServiceProvider? _serviceProvider;
    private IDbContextFactory<TDbContext>? _factory;
    private string? _isolatedConnectionString;

    protected EFCoreMembershipTableTestsBase(
        ConnectionStringFixture fixture,
        TestEnvironmentFixture environment)
        : base(fixture, environment, CreateFilters())
    {
    }

    protected abstract EFCoreTestDatabase Database { get; }

    protected abstract IEFClusterETagConverter<TETag> CreateETagConverter();

    protected override IMembershipTable CreateMembershipTable(ILogger logger) =>
        CreateMembershipTable(clusterId);

    protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger) =>
        CreateGatewayListProvider(clusterId, _gatewayOptions);

    protected override Task<string> GetConnectionString()
    {
        _isolatedConnectionString ??= Database.WithDatabase(
            Database.RequireConnectionString(),
            Database.CreateDatabaseName("membership", GetTargetFramework()));

        return Task.FromResult(_isolatedConnectionString);
    }

    [SkippableFact]
    public async Task MembershipTable_InitializationCreatesReadableVersion()
    {
        var data = await CreateMembershipTable(clusterId).ReadAll();

        Assert.Empty(data.Members);
        Assert.Equal(0, data.Version.Version);
        Assert.False(string.IsNullOrWhiteSpace(data.Version.VersionEtag));
    }

    [SkippableFact]
    public Task MembershipTable_GetGateways_UsesActiveNonZeroProxyPorts() =>
        MembershipTable_GetGateways();

    [SkippableFact]
    public Task MembershipTable_ReadAll_EmptyTable_ReturnsInitialVersion() =>
        MembershipTable_ReadAll_EmptyTable();

    [SkippableFact]
    public Task MembershipTable_InsertRow_AdvancesVersionAndAddsMember() =>
        MembershipTable_InsertRow();

    [SkippableFact]
    public Task MembershipTable_ReadRow_RejectsDuplicatesAndStaleVersion() =>
        MembershipTable_ReadRow_Insert_Read();

    [SkippableFact]
    public Task MembershipTable_ReadAll_ReturnsInsertedMemberAndETags() =>
        MembershipTable_ReadAll_Insert_ReadAll();

    [SkippableFact]
    public Task MembershipTable_UpdateRow_EnforcesRowAndTableETags() =>
        MembershipTable_UpdateRow();

    [SkippableFact]
    public Task MembershipTable_ParallelUpdates_AdvanceEverySuccessfulVersion() =>
        MembershipTable_UpdateRowInParallel();

    [SkippableFact]
    public Task MembershipTable_UpdateIAmAlive_PreservesTableVersion() =>
        MembershipTable_UpdateIAmAlive();

    [SkippableFact]
    public Task MembershipTable_CleanupDefunctSilos_PreservesActiveAndRecentMembers() =>
        MembershipTable_CleanupDefunctSiloEntries();

    [SkippableFact]
    public async Task InitializeMembershipTable_False_DoesNotCreateVersionTwice()
    {
        var table = CreateMembershipTable(clusterId);
        ClusterRecord<TETag> before;

        await using (var context = await Factory.CreateDbContextAsync())
        {
            before = await context.Clusters.AsNoTracking().SingleAsync(record => record.Id == clusterId);
        }

        var beforeETag = CreateETagConverter().FromDbETag(before.ETag);
        await table.InitializeMembershipTable(false);

        await using var verification = await Factory.CreateDbContextAsync();
        var clusters = await verification.Clusters
            .AsNoTracking()
            .Where(record => record.Id == clusterId)
            .ToListAsync();
        var after = Assert.Single(clusters);

        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Timestamp, after.Timestamp);
        Assert.Equal(beforeETag, CreateETagConverter().FromDbETag(after.ETag));
    }

    [SkippableFact]
    public async Task InitializeMembershipTable_ConcurrentFirstInitializationSucceeds()
    {
        var raceClusterId = $"initialization-race-{Guid.NewGuid():N}";
        var tables = Enumerable.Range(0, 16)
            .Select(_ => CreateMembershipTable(raceClusterId))
            .ToArray();

        await Task.WhenAll(tables.Select(table => table.InitializeMembershipTable(true)));

        await using var verification = await Factory.CreateDbContextAsync();
        var cluster = Assert.Single(await verification.Clusters
            .AsNoTracking()
            .Where(record => record.Id == raceClusterId)
            .ToListAsync());
        Assert.Equal(0, cluster.Version);
        Assert.False(string.IsNullOrWhiteSpace(CreateETagConverter().FromDbETag(cluster.ETag)));
    }

    [SkippableFact]
    public async Task GatewayProvider_ExposesConfiguredMaxStalenessAndIsUpdatable()
    {
        var refreshPeriod = TimeSpan.FromSeconds(37);
        var provider = CreateGatewayListProvider(
            clusterId,
            Options.Create(new GatewayOptions { GatewayListRefreshPeriod = refreshPeriod }));

        await provider.InitializeGatewayListProvider();
        var gateways = await provider.GetGateways();

        Assert.True(provider.IsUpdatable);
        Assert.Equal(refreshPeriod, provider.MaxStaleness);
        Assert.Empty(gateways);
    }

    [SkippableFact]
    public async Task DeleteMembershipTableEntries_OnlyDeletesRequestedCluster()
    {
        var primaryTable = CreateMembershipTable(clusterId);
        var secondaryClusterId = $"secondary-{Guid.NewGuid():N}";
        var secondaryTable = CreateMembershipTable(secondaryClusterId);
        await secondaryTable.InitializeMembershipTable(true);

        var primaryEntry = CreateMembershipEntry(IPAddress.Loopback, 24101);
        var secondaryEntry = CreateMembershipEntry(IPAddress.Parse("127.0.0.2"), 24102);
        Assert.True(await Insert(primaryTable, primaryEntry));
        Assert.True(await Insert(secondaryTable, secondaryEntry));

        await primaryTable.DeleteMembershipTableEntries(secondaryClusterId);

        await using var context = await Factory.CreateDbContextAsync();
        Assert.True(await context.Clusters.AnyAsync(record => record.Id == clusterId));
        Assert.Single(await context.Silos.Where(record => record.ClusterId == clusterId).ToListAsync());
        Assert.False(await context.Clusters.AnyAsync(record => record.Id == secondaryClusterId));
        Assert.False(await context.Silos.AnyAsync(record => record.ClusterId == secondaryClusterId));

        var retained = await primaryTable.ReadAll();
        Assert.Single(retained.Members);
        Assert.Equal(1, retained.Version.Version);
    }

    [SkippableFact]
    public async Task SuspectingLists_RoundTripSpecialValues()
    {
        var table = CreateMembershipTable(clusterId);
        var entry = CreateMembershipEntry(IPAddress.Parse("192.0.2.10"), 24103);
        var firstSuspector = SiloAddress.New(
            new IPEndPoint(IPAddress.Parse("2001:db8::7"), 30111),
            Interlocked.Increment(ref _generation));
        var secondSuspector = SiloAddress.New(
            new IPEndPoint(IPAddress.Parse("198.51.100.42"), 30112),
            Interlocked.Increment(ref _generation));
        var firstTime = new DateTime(2024, 2, 29, 23, 59, 58, DateTimeKind.Utc);
        var secondTime = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        entry.AddSuspector(firstSuspector, firstTime);
        entry.AddSuspector(secondSuspector, secondTime);

        Assert.True(await Insert(table, entry));

        var data = await table.ReadRow(entry.SiloAddress);
        var stored = Assert.Single(data.Members);
        var suspectors = stored.Item1.SuspectTimes;

        Assert.NotNull(suspectors);
        Assert.Collection(
            suspectors,
            suspect =>
            {
                Assert.Equal(firstSuspector, suspect.Item1);
                Assert.Equal(firstTime, suspect.Item2);
            },
            suspect =>
            {
                Assert.Equal(secondSuspector, suspect.Item1);
                Assert.Equal(secondTime, suspect.Item2);
            });
        Assert.Equal(entry.HostName, stored.Item1.HostName);
        Assert.Equal(entry.SiloName, stored.Item1.SiloName);
        Assert.Equal(entry.Status, stored.Item1.Status);
        Assert.False(string.IsNullOrWhiteSpace(stored.Item2));
        Assert.Equal(1, data.Version.Version);
    }

    void IDisposable.Dispose()
    {
        base.Dispose();

        if (_factory is not null)
        {
            Database.DeleteDatabaseAsync(_factory).GetAwaiter().GetResult();
        }

        _serviceProvider?.Dispose();
    }

    private IDbContextFactory<TDbContext> Factory
    {
        get
        {
            if (_factory is not null)
            {
                return _factory;
            }

            _serviceProvider = new ServiceCollection()
                .AddPooledDbContextFactory<TDbContext>(
                    options => Database.ConfigureOptions(
                        options,
                        connectionString,
                        typeof(TDbContext).Assembly.GetName().Name!))
                .BuildServiceProvider();
            _factory = _serviceProvider.GetRequiredService<IDbContextFactory<TDbContext>>();
            Database.MigrateAsync(_factory).GetAwaiter().GetResult();
            return _factory;
        }
    }

    private IMembershipTable CreateMembershipTable(string targetClusterId) =>
        new EFMembershipTable<TDbContext, TETag>(
            loggerFactory,
            Options.Create(new ClusterOptions { ClusterId = targetClusterId }),
            Factory,
            CreateETagConverter());

    private EFGatewayListProvider<TDbContext, TETag> CreateGatewayListProvider(
        string targetClusterId,
        IOptions<GatewayOptions> gatewayOptions) =>
        new EFGatewayListProvider<TDbContext, TETag>(
            loggerFactory,
            Options.Create(new ClusterOptions { ClusterId = targetClusterId }),
            gatewayOptions,
            Factory);

    private static async Task<bool> Insert(IMembershipTable table, MembershipEntry entry)
    {
        var current = await table.ReadAll();
        return await table.InsertRow(entry, current.Version.Next());
    }

    private static MembershipEntry CreateMembershipEntry(IPAddress address, int port)
    {
        var now = new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        var siloAddress = SiloAddress.New(
            new IPEndPoint(address, port),
            Interlocked.Increment(ref _generation));

        return new MembershipEntry
        {
            SiloAddress = siloAddress,
            HostName = "membership-host.example",
            SiloName = "membership-silo",
            Status = SiloStatus.Joining,
            ProxyPort = port + 100,
            StartTime = now,
            IAmAliveTime = now.AddMinutes(1)
        };
    }

    private static LoggerFilterOptions CreateFilters()
    {
        var filters = new LoggerFilterOptions();
        filters.AddFilter(typeof(EFCoreMembershipTableTestsBase<TDbContext, TETag>).FullName, LogLevel.Trace);
        return filters;
    }

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
}
