#nullable enable

using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Internal;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using Orleans.Runtime;

namespace Orleans.DurableJobs.Tests;

public interface IJobShardManagerTestFixture
{
    Task<IJobShardManagerTestScope> CreateScopeAsync();
}

public interface IJobShardManagerTestScope : IAsyncDisposable
{
    TestSilo ActiveSilo { get; }

    TestSilo SecondActiveSilo { get; }

    TestSilo ThirdActiveSilo { get; }

    TestSilo FormerOwnerSilo { get; }

    DateTimeOffset Now { get; }

    JobShardManager CreateManager(TestSilo silo, DurableJobsOptions? options = null);

    void SetSiloStatus(TestSilo silo, SiloStatus status);
}

public sealed record TestSilo(SiloAddress SiloAddress);

public sealed class VolatileJobShardManagerTestFixture : IJobShardManagerTestFixture
{
    public Task<IJobShardManagerTestScope> CreateScopeAsync()
    {
        var builder = new TestSiloBuilder();
        builder.AddJournalStorage();
        builder.UseJsonJournalFormat(options => options.AddTypeInfoResolver(DurableJobsJsonContext.Default));
        builder.Services.AddLogging();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, static (sp, _) => sp.GetRequiredService<TimeProvider>());
        builder.Services.AddSingleton<VolatileJournalStorageProvider>();
        builder.Services.AddFromExisting<IJournalStorageProvider, VolatileJournalStorageProvider>();
        builder.Services.AddFromExisting<IJournalStorageCatalog, VolatileJournalStorageProvider>();

        return Task.FromResult<IJobShardManagerTestScope>(new JournaledJobShardManagerTestScope(builder.Services.BuildServiceProvider()));
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}

public class JournaledJobShardManagerTestScope : IJobShardManagerTestScope
{
    private static int _nextPort = 40_000;
    private readonly ServiceProvider _services;
    private readonly TestClusterMembershipService _membership = new();

    public JournaledJobShardManagerTestScope(ServiceProvider services)
    {
        _services = services;
        ActiveSilo = CreateSilo();
        SecondActiveSilo = CreateSilo();
        ThirdActiveSilo = CreateSilo();
        FormerOwnerSilo = CreateSilo();

        SetSiloStatus(ActiveSilo, SiloStatus.Active);
        SetSiloStatus(SecondActiveSilo, SiloStatus.Active);
        SetSiloStatus(ThirdActiveSilo, SiloStatus.Active);
        SetSiloStatus(FormerOwnerSilo, SiloStatus.Active);
    }

    public TestSilo ActiveSilo { get; }

    public TestSilo SecondActiveSilo { get; }

    public TestSilo ThirdActiveSilo { get; }

    public TestSilo FormerOwnerSilo { get; }

    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public JobShardManager CreateManager(TestSilo silo, DurableJobsOptions? options = null)
        => new JournaledJobShardManager(
            new TestLocalSiloDetails(silo.SiloAddress),
            _services.GetRequiredService<IJournaledStateManagerFactory>(),
            _services.GetRequiredService<IJournalStorageProvider>(),
            _services.GetRequiredService<IJournalStorageCatalog>(),
            _membership,
            _services,
            Options.Create(options ?? new DurableJobsOptions()),
            _services.GetRequiredService<IOptions<JournaledStateManagerOptions>>());

    public void SetSiloStatus(TestSilo silo, SiloStatus status) => _membership.SetSiloStatus(silo.SiloAddress, status);

    public virtual ValueTask DisposeAsync()
    {
        _services.Dispose();
        return ValueTask.CompletedTask;
    }

    private static TestSilo CreateSilo()
    {
        var port = Interlocked.Increment(ref _nextPort);
        return new(SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), port));
    }

    private sealed class TestLocalSiloDetails(SiloAddress siloAddress) : ILocalSiloDetails
    {
        public string Name => SiloAddress.ToParsableString();

        public string ClusterId => "TestCluster";

        public string DnsHostName => SiloAddress.ToParsableString();

        public SiloAddress SiloAddress { get; } = siloAddress;

        public SiloAddress GatewayAddress => SiloAddress;
    }

    private sealed class TestClusterMembershipService : IClusterMembershipService
    {
        private ImmutableDictionary<SiloAddress, ClusterMember> _members = ImmutableDictionary<SiloAddress, ClusterMember>.Empty;
        private long _version;

        public ClusterMembershipSnapshot CurrentSnapshot => new(_members, new MembershipVersion(_version));

        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => GetMembershipUpdates();

        public void SetSiloStatus(SiloAddress siloAddress, SiloStatus status)
        {
            _members = _members.SetItem(siloAddress, new ClusterMember(siloAddress, status, siloAddress.ToParsableString()));
            _version++;
        }

        public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public Task<bool> TryKill(SiloAddress siloAddress) => Task.FromResult(false);

        private static async IAsyncEnumerable<ClusterMembershipSnapshot> GetMembershipUpdates()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

}
