using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization;
using Xunit;

namespace UnitTests.MembershipTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT"), TestCategory("Membership")]
public sealed class MembershipTableSystemTargetTests : IDisposable
{
    private const string ClusterId = "own-cluster";
    private readonly ServiceProvider _services;
    private readonly MembershipTableSystemTarget _target;
    private readonly CancellationToken _cancellationToken = TestContext.Current.CancellationToken;

    public MembershipTableSystemTargetTests()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddLogging();
        services.AddMetrics();
        services.Configure<ClusterOptions>(options => options.ClusterId = ClusterId);
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton<SchedulerInstruments>();
        services.AddSingleton<CatalogInstruments>();
        services.AddSingleton<GrainInstruments>();
        services.AddSingleton<MessagingInstruments>();
        services.AddSingleton<MessagingProcessingInstruments>();
        _services = services.BuildServiceProvider();

        var localSiloDetails = Substitute.For<ILocalSiloDetails>();
        localSiloDetails.SiloAddress.Returns(SiloAddress.New(IPAddress.Loopback, 11111, 1));
        var shared = new SystemTargetShared(
            runtimeClient: null!,
            localSiloDetails: localSiloDetails,
            loggerFactory: NullLoggerFactory.Instance,
            schedulingOptions: Options.Create(new SchedulingOptions()),
            grainReferenceActivator: null!,
            timerRegistry: null!,
            activations: new ActivationDirectory(_services.GetRequiredService<CatalogInstruments>()),
            schedulerInstruments: _services.GetRequiredService<SchedulerInstruments>(),
            grainInstruments: _services.GetRequiredService<GrainInstruments>(),
            messagingInstruments: _services.GetRequiredService<MessagingInstruments>(),
            messagingProcessingInstruments: _services.GetRequiredService<MessagingProcessingInstruments>());
        _target = ActivatorUtilities.CreateInstance<MembershipTableSystemTarget>(_services, shared);
    }

    [Theory]
    [InlineData("foreign-cluster")]
    [InlineData("OWN-CLUSTER")]
    public async Task DeleteMembershipTableEntries_ForeignCluster_PreservesTable(string clusterId)
    {
        var before = await SeedTable();

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => _target.DeleteMembershipTableEntriesAsync(clusterId, _cancellationToken));

        Assert.Equal("clusterId", exception.ParamName);
        AssertUnchanged(before, await _target.ReadAllAsync(_cancellationToken));
        var entry = Assert.Single(before.Members).Item1;
        AssertUnchanged(before, await _target.ReadRowAsync(entry.SiloAddress, _cancellationToken));
    }

    [Fact]
    public async Task DeleteMembershipTableEntries_OwnCluster_TerminallyInvalidatesTable()
    {
        var before = await SeedTable();
        var entry = Assert.Single(before.Members).Item1;

        await _target.DeleteMembershipTableEntriesAsync(ClusterId, _cancellationToken);

        // Deletion ends this target's table lifetime, including after another initialization request.
        await Assert.ThrowsAsync<NullReferenceException>(() => _target.ReadAllAsync(_cancellationToken));
        await _target.InitializeMembershipTableAsync(true, _cancellationToken);
        await Assert.ThrowsAsync<NullReferenceException>(() => _target.ReadRowAsync(entry.SiloAddress, _cancellationToken));
        await Assert.ThrowsAsync<NullReferenceException>(() => _target.InsertRowAsync(entry, before.Version.Next(), _cancellationToken));
    }

    [Theory]
    [InlineData(ClusterId)]
    [InlineData("foreign-cluster")]
    public async Task DeleteMembershipTableEntries_Canceled_PreservesTableBeforeScopeValidation(string clusterId)
    {
        var before = await SeedTable();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => _target.DeleteMembershipTableEntriesAsync(clusterId, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        AssertUnchanged(before, await _target.ReadAllAsync(_cancellationToken));
    }

    private async Task<MembershipTableData> SeedTable()
    {
        await _target.InitializeMembershipTableAsync(true, _cancellationToken);
        var before = await _target.ReadAllAsync(_cancellationToken);
        var entry = new MembershipEntry
        {
            SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
            HostName = "localhost",
            SiloName = "test-silo",
            Status = SiloStatus.Active,
            StartTime = DateTime.UnixEpoch,
            IAmAliveTime = DateTime.UnixEpoch.AddMinutes(1)
        };
        Assert.True(await _target.InsertRowAsync(entry, before.Version.Next(), _cancellationToken));
        return await _target.ReadAllAsync(_cancellationToken);
    }

    private static void AssertUnchanged(MembershipTableData expected, MembershipTableData actual)
    {
        Assert.Equal(expected.Version, actual.Version);
        var expectedRow = Assert.Single(expected.Members);
        var actualRow = Assert.Single(actual.Members);
        Assert.Equal(expectedRow.Item2, actualRow.Item2);
        Assert.Equal(expectedRow.Item1.SiloAddress, actualRow.Item1.SiloAddress);
        Assert.Equal(expectedRow.Item1.HostName, actualRow.Item1.HostName);
        Assert.Equal(expectedRow.Item1.SiloName, actualRow.Item1.SiloName);
        Assert.Equal(expectedRow.Item1.Status, actualRow.Item1.Status);
        Assert.Equal(expectedRow.Item1.StartTime, actualRow.Item1.StartTime);
        Assert.Equal(expectedRow.Item1.IAmAliveTime, actualRow.Item1.IAmAliveTime);
    }

    public void Dispose()
    {
        _target.Dispose();
        _services.Dispose();
    }
}
