using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans;
using Orleans.Clustering.TestKit;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.Serialization;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.MembershipTests;

[TestCategory("BVT"), TestCategory("Membership")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public sealed class SystemTargetMembershipTableConformanceTests : MembershipTableConformanceTestsBase, IAsyncLifetime
{
    private readonly object _progressLock = new();
    private readonly string _progressFile = Path.Combine(AppContext.BaseDirectory, "TestResults", $"hosted-membership-{Guid.NewGuid():N}.log");
    private readonly PeriodicTimer _progressTimer = new(TimeSpan.FromSeconds(1));
    private Task _progressWriter = Task.CompletedTask;
    private string _modelProgress = "case not started";
    private string _nativeProgress = "host not started";
    private long _modelPhaseStarted = Stopwatch.GetTimestamp();
    private long _nativePhaseStarted = Stopwatch.GetTimestamp();

    [Theory, TestCategory("ModelBased")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public Task MembershipTable_ModelBased_GeneratedConformance(int partition)
        => CreateModelBasedRunner().RunGeneratedConformanceTests(partition, 4, TestContext.Current.CancellationToken);

    public ValueTask InitializeAsync()
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(_progressFile)!);
        _progressWriter = WriteProgressPeriodically();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _progressTimer.Dispose();
        await _progressWriter;
        await WriteProgress();
    }

    protected override void WriteConformanceOutput(string message)
    {
        lock (_progressLock)
        {
            _modelProgress = $"{message}; previous-phase-elapsed-ms={Stopwatch.GetElapsedTime(_modelPhaseStarted).TotalMilliseconds:F1}";
            _modelPhaseStarted = Stopwatch.GetTimestamp();
        }
        if (!message.Contains("; phase=", StringComparison.Ordinal))
        {
            base.WriteConformanceOutput(message);
        }
    }

    private void ReportNativeProgress(string message)
    {
        lock (_progressLock)
        {
            _nativeProgress = $"{message}; previous-phase-elapsed-ms={Stopwatch.GetElapsedTime(_nativePhaseStarted).TotalMilliseconds:F1}";
            _nativePhaseStarted = Stopwatch.GetTimestamp();
        }
    }

    private async Task WriteProgressPeriodically()
    {
        do
        {
            await WriteProgress();
        }
        while (await _progressTimer.WaitForNextTickAsync());
    }

    private Task WriteProgress()
    {
        string snapshot;
        lock (_progressLock)
        {
            snapshot = $"{DateTime.UtcNow:O}{Environment.NewLine}"
                + $"{_modelProgress}; elapsed-ms={Stopwatch.GetElapsedTime(_modelPhaseStarted).TotalMilliseconds:F1}{Environment.NewLine}"
                + $"{_nativeProgress}; elapsed-ms={Stopwatch.GetElapsedTime(_nativePhaseStarted).TotalMilliseconds:F1}{Environment.NewLine}";
        }

        // Sample the latest phase for watchdog artifacts without adding file I/O to each operation.
        return File.WriteAllTextAsync(_progressFile, snapshot);
    }

    protected override MembershipTableTestFixture CreateConformanceFixture()
    {
        var targets = new Dictionary<string, TargetScope>(StringComparer.Ordinal);
        return new MembershipTableTestFixture(
            nameof(SystemTargetBasedMembershipTable),
            async (serviceId, clusterId, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!targets.TryGetValue(clusterId, out var target))
                {
                    target = await TargetScope.CreateAsync(serviceId, clusterId, ReportNativeProgress, cancellationToken);
                    targets.Add(clusterId, target);
                }

                return target.CreateClient();
            },
            (clusterId, cancellationToken) => targets[clusterId].IsDeletedAsync(cancellationToken));
    }

    private sealed class TargetScope
    {
        private static readonly FieldInfo TableField = typeof(MembershipTableSystemTarget)
            .GetField("table", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(MembershipTableSystemTarget).FullName, "table");
        private readonly InProcessTestCluster _cluster;
        private readonly InProcessSiloHandle _silo;
        private readonly MembershipTableSystemTarget _target;
        private readonly Action<string> _progress;
        private readonly string _clusterId;
        private int _clientCount;

        private TargetScope(InProcessTestCluster cluster, string clusterId, Action<string> progress)
        {
            _cluster = cluster;
            _clusterId = clusterId;
            _progress = progress;
            _silo = Assert.Single(cluster.Silos);
            _target = _silo.ServiceProvider.GetRequiredService<MembershipTableSystemTarget>();
            Assert.Equal(typeof(InMemoryMembershipTable), TableField.FieldType);
            Assert.IsType<InMemoryMembershipTable>(TableField.GetValue(_target));
        }

        public static async Task<TargetScope> CreateAsync(string serviceId, string clusterId, Action<string> progress, CancellationToken cancellationToken)
        {
            var builder = new InProcessTestClusterBuilder(1);
            builder.Options.ServiceId = serviceId;
            builder.Options.ClusterId = $"host-{clusterId}";
            builder.Options.ConfigureFileLogging = false;
            builder.Options.InitializeClientOnDeploy = false;
            builder.ConfigureSiloHost((_, host) =>
            {
                host.Services.AddSingleton(provider => new MembershipTableSystemTarget(
                    provider.GetRequiredService<ILogger<MembershipTableSystemTarget>>(),
                    provider.GetRequiredService<DeepCopier>(),
                    provider.GetRequiredService<SystemTargetShared>(),
                    Options.Create(new ClusterOptions { ServiceId = serviceId, ClusterId = clusterId })));
                host.Services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(
                    provider => provider.GetRequiredService<MembershipTableSystemTarget>());
            });

            var cluster = builder.Build();
            try
            {
                progress($"cluster={clusterId}; phase=host-deploy");
                await cluster.DeployAsync(cancellationToken);
                var scope = new TargetScope(cluster, clusterId, progress);
                progress($"cluster={clusterId}; phase=host-deployed");
                progress($"cluster={clusterId}; phase=host-verify");
                await scope.AssertHostMembershipIsIntact(cancellationToken);
                var proxy = scope.GetGrainFactory().GetSystemTarget<IMembershipTableSystemTarget>(
                    Constants.SystemMembershipTableType, SiloAddress.New(scope._silo.SiloAddress.Endpoint, 0));
                Assert.IsAssignableFrom<GrainReference>(proxy);
                progress($"cluster={clusterId}; phase=target-verify");
                Assert.Empty((await proxy.ReadAllAsync(cancellationToken)).Members);
                progress($"cluster={clusterId}; phase=host-ready");
                return scope;
            }
            catch (Exception failure)
            {
                try
                {
                    progress($"cluster={clusterId}; phase=failed-host-dispose");
                    await cluster.DisposeAsync();
                    progress($"cluster={clusterId}; phase=failed-host-disposed");
                }
                catch (Exception cleanup)
                {
                    failure.Data["ClusterDisposalFailure"] = cleanup;
                }

                throw;
            }
        }

        public MembershipTableTestHandle CreateClient()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IOptions<DevelopmentClusterMembershipOptions>>(
                Options.Create(new DevelopmentClusterMembershipOptions { PrimarySiloEndpoint = _silo.SiloAddress.Endpoint }));
            services.AddSingleton(_silo.ServiceProvider.GetRequiredService<ILocalSiloDetails>());
            services.AddSingleton(GetGrainFactory());
            // The passive owner starts in this test table's empty history, independently of host membership.
            var owner = Substitute.For<IMembershipManager>();
            owner.CurrentSnapshot.Returns(new MembershipTableSnapshot(
                new MembershipVersion(0), ImmutableDictionary<SiloAddress, MembershipEntry>.Empty));
            services.AddSingleton(owner);
            var fatalErrorHandler = Substitute.For<IFatalErrorHandler>();
            var clientServices = services.BuildServiceProvider();
            var client = new SystemTargetBasedMembershipTable(
                clientServices,
                NullLogger<SystemTargetBasedMembershipTable>.Instance,
                fatalErrorHandler);
            Interlocked.Increment(ref _clientCount);
            return new MembershipTableTestHandle(client, async () =>
            {
                try
                {
                    Assert.Empty(fatalErrorHandler.ReceivedCalls());
                }
                finally
                {
                    await clientServices.DisposeAsync();
                    if (Interlocked.Decrement(ref _clientCount) == 0)
                    {
                        try
                        {
                            _progress($"cluster={_clusterId}; phase=host-verify-before-dispose; clients={_clientCount}");
                            await AssertHostMembershipIsIntact(CancellationToken.None);
                        }
                        finally
                        {
                            _progress($"cluster={_clusterId}; phase=host-dispose; clients={_clientCount}");
                            await _cluster.DisposeAsync();
                            _progress($"cluster={_clusterId}; phase=host-disposed; clients={_clientCount}");
                        }
                    }
                }
            });
        }

        public async ValueTask<bool> IsDeletedAsync(CancellationToken cancellationToken)
        {
            await AssertHostMembershipIsIntact(cancellationToken);
            Assert.Same(_target, _silo.ServiceProvider.GetRequiredService<MembershipTableSystemTarget>());
            return TableField.GetValue(_target) is null;
        }

        private IInternalGrainFactory GetGrainFactory() => _silo.ServiceProvider.GetRequiredService<IInternalGrainFactory>();

        private async Task AssertHostMembershipIsIntact(CancellationToken cancellationToken)
        {
            var membership = _silo.ServiceProvider.GetRequiredService<IMembershipTable>();
            Assert.Equal("InProcessMembershipTable", membership.GetType().Name);
            var row = Assert.Single((await membership.ReadAllAsync(cancellationToken)).Members).Item1;
            Assert.Equal(_silo.SiloAddress, row.SiloAddress);
            Assert.Equal(SiloStatus.Active, row.Status);
        }
    }
}
