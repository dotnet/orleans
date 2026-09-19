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

    [Fact]
    public async Task WriteResults_CaptureReceiptsInOriginalSchedulerTurn()
    {
        var context = (IGrainContext)_target;
        var first = new MembershipEntry
        {
            SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 2),
            HostName = "first-host",
            SiloName = "first-silo",
            Status = SiloStatus.Joining,
            StartTime = DateTime.UnixEpoch,
            IAmAliveTime = DateTime.UnixEpoch.AddMinutes(1)
        };
        var second = new MembershipEntry
        {
            SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 3),
            HostName = "second-host",
            SiloName = "second-silo",
            Status = SiloStatus.Active,
            StartTime = DateTime.UnixEpoch.AddMinutes(2),
            IAmAliveTime = DateTime.UnixEpoch.AddMinutes(3)
        };
        MembershipTableWriteResult inserted = default;
        MembershipTableWriteResult updated = default;
        MembershipTableWriteResult secondInserted = default;
        MembershipTableWriteResult laterUpdated = default;
        (int Version, string TableTag, string RowTag) insertBaseline = default;
        (int Version, string TableTag, string RowTag) updateBaseline = default;
        (int Version, string TableTag, string RowTag) secondBaseline = default;

        await context.QueueTask(async () =>
        {
            Assert.Same(context, RuntimeContext.Current);
            await _target.InitializeMembershipTableAsync(true, _cancellationToken);
            var seed = await _target.ReadAllAsync(_cancellationToken);
            Assert.Empty(seed.Members);
            Assert.Equal(new TableVersion(0, "0"), seed.Version);

            // Capture the native result here, before another mutation or scheduler turn.
            var insertTask = _target.InsertRowWithResultAsync(first, seed.Version.Next(), _cancellationToken);
            Assert.True(insertTask.IsCompletedSuccessfully);
            inserted = await insertTask;
            Assert.True(inserted.Succeeded);
            var insertReceipt = Assert.IsType<MembershipTableWriteReceipt>(inserted.Receipt);
            insertBaseline = (insertReceipt.Version.Version, insertReceipt.Version.VersionEtag, insertReceipt.RowETag);
            Assert.Equal((1, "2", "1"), insertBaseline);

            first.Status = SiloStatus.ShuttingDown;
            var updateTask = _target.UpdateRowWithResultAsync(
                first, insertReceipt.RowETag, insertReceipt.Version.Next(), _cancellationToken);
            Assert.True(updateTask.IsCompletedSuccessfully);
            updated = await updateTask;
            Assert.True(updated.Succeeded);
            var updateReceipt = Assert.IsType<MembershipTableWriteReceipt>(updated.Receipt);
            updateBaseline = (updateReceipt.Version.Version, updateReceipt.Version.VersionEtag, updateReceipt.RowETag);
            Assert.Equal((2, "4", "3"), updateBaseline);

            var secondTask = _target.InsertRowWithResultAsync(second, updateReceipt.Version.Next(), _cancellationToken);
            Assert.True(secondTask.IsCompletedSuccessfully);
            secondInserted = await secondTask;
            Assert.True(secondInserted.Succeeded);
            var secondReceipt = Assert.IsType<MembershipTableWriteReceipt>(secondInserted.Receipt);
            secondBaseline = (secondReceipt.Version.Version, secondReceipt.Version.VersionEtag, secondReceipt.RowETag);
            Assert.Equal((3, "6", "5"), secondBaseline);
            Assert.Same(context, RuntimeContext.Current);
        });

        await context.QueueTask(async () =>
        {
            Assert.Same(context, RuntimeContext.Current);
            first.IAmAliveTime = DateTime.UnixEpoch.AddMinutes(5);
            await _target.UpdateIAmAliveAsync(first, _cancellationToken);
            second.Status = SiloStatus.Dead;
            var receipt = Assert.IsType<MembershipTableWriteReceipt>(secondInserted.Receipt);
            var writeTask = _target.UpdateRowWithResultAsync(
                second, receipt.RowETag, receipt.Version.Next(), _cancellationToken);
            Assert.True(writeTask.IsCompletedSuccessfully);
            laterUpdated = await writeTask;
            Assert.Same(context, RuntimeContext.Current);
        });

        // No read occurred between any writes. These are the raw receipts retained above.
        var originalInsert = Assert.IsType<MembershipTableWriteReceipt>(inserted.Receipt);
        var originalUpdate = Assert.IsType<MembershipTableWriteReceipt>(updated.Receipt);
        var originalSecond = Assert.IsType<MembershipTableWriteReceipt>(secondInserted.Receipt);
        Assert.Equal(insertBaseline, (originalInsert.Version.Version, originalInsert.Version.VersionEtag, originalInsert.RowETag));
        Assert.Equal(updateBaseline, (originalUpdate.Version.Version, originalUpdate.Version.VersionEtag, originalUpdate.RowETag));
        Assert.Equal(secondBaseline, (originalSecond.Version.Version, originalSecond.Version.VersionEtag, originalSecond.RowETag));
        Assert.True(laterUpdated.Succeeded);
        var finalReceipt = Assert.IsType<MembershipTableWriteReceipt>(laterUpdated.Receipt);
        Assert.Equal(new TableVersion(4, "8"), finalReceipt.Version);
        Assert.Equal("7", finalReceipt.RowETag);

        var final = await _target.ReadAllAsync(_cancellationToken);
        Assert.Equal(finalReceipt.Version, final.Version);
        Assert.Equal(2, final.Members.Count);
        var firstRow = Assert.IsType<Tuple<MembershipEntry, string>>(final.TryGet(first.SiloAddress));
        Assert.Equal(originalUpdate.RowETag, firstRow.Item2);
        Assert.Equal("first-host", firstRow.Item1.HostName);
        Assert.Equal("first-silo", firstRow.Item1.SiloName);
        Assert.Equal(SiloStatus.ShuttingDown, firstRow.Item1.Status);
        Assert.Equal(DateTime.UnixEpoch, firstRow.Item1.StartTime);
        Assert.Equal(DateTime.UnixEpoch.AddMinutes(5), firstRow.Item1.IAmAliveTime);
        var secondRow = Assert.IsType<Tuple<MembershipEntry, string>>(final.TryGet(second.SiloAddress));
        Assert.Equal(finalReceipt.RowETag, secondRow.Item2);
        Assert.Equal("second-host", secondRow.Item1.HostName);
        Assert.Equal("second-silo", secondRow.Item1.SiloName);
        Assert.Equal(SiloStatus.Dead, secondRow.Item1.Status);
        Assert.Equal(DateTime.UnixEpoch.AddMinutes(2), secondRow.Item1.StartTime);
        Assert.Equal(DateTime.UnixEpoch.AddMinutes(3), secondRow.Item1.IAmAliveTime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteResult_ConditionalFailure_ReturnsNoReceipt(bool update)
    {
        var before = await SeedTable();
        var row = Assert.Single(before.Members);
        var changed = row.Item1.Copy();
        changed.Status = SiloStatus.ShuttingDown;
        changed.HostName = "changed-host";
        changed.IAmAliveTime = DateTime.UnixEpoch.AddMinutes(4);
        var context = (IGrainContext)_target;
        MembershipTableWriteResult rejected = default;

        await context.QueueTask(async () =>
        {
            // Duplicate insert with current table CAS, or valid row guard with stale table CAS.
            var operation = update
                ? _target.UpdateRowWithResultAsync(
                    changed, row.Item2, new TableVersion(before.Version.Version + 1, "stale-table-tag"), _cancellationToken)
                : _target.InsertRowWithResultAsync(changed, before.Version.Next(), _cancellationToken);
            Assert.True(operation.IsCompletedSuccessfully);
            rejected = await operation;
        });

        Assert.False(rejected.Succeeded);
        Assert.Null(rejected.Receipt);
        AssertUnchanged(before, await _target.ReadAllAsync(_cancellationToken));

        MembershipTableWriteResult committed = default;
        await context.QueueTask(async () =>
        {
            var operation = _target.UpdateRowWithResultAsync(changed, row.Item2, before.Version.Next(), _cancellationToken);
            Assert.True(operation.IsCompletedSuccessfully);
            committed = await operation;
        });

        Assert.True(committed.Succeeded);
        var receipt = Assert.IsType<MembershipTableWriteReceipt>(committed.Receipt);
        Assert.Equal(new TableVersion(2, "4"), receipt.Version);
        Assert.Equal("3", receipt.RowETag);
        AssertUnchanged(
            new MembershipTableData(Tuple.Create(changed, receipt.RowETag), receipt.Version),
            await _target.ReadAllAsync(_cancellationToken));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PreCanceledWriteResult_PreservesTable(bool update, bool terminallyDeleted)
    {
        var before = await SeedTable();
        var row = Assert.Single(before.Members);
        var changed = row.Item1.Copy();
        changed.Status = SiloStatus.Dead;
        changed.HostName = "must-not-be-stored";
        if (!update)
        {
            changed.SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 2);
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
        cancellation.Cancel();
        var context = (IGrainContext)_target;

        async Task AssertCanceledWrite()
        {
            var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => update
                ? _target.UpdateRowWithResultAsync(changed, row.Item2, before.Version.Next(), cancellation.Token)
                : _target.InsertRowWithResultAsync(changed, before.Version.Next(), cancellation.Token));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }

        await context.QueueTask(AssertCanceledWrite);
        AssertUnchanged(before, await _target.ReadAllAsync(_cancellationToken));

        if (terminallyDeleted)
        {
            await _target.DeleteMembershipTableEntriesAsync(ClusterId, _cancellationToken);
            await context.QueueTask(AssertCanceledWrite);
            await Assert.ThrowsAsync<NullReferenceException>(() => _target.ReadAllAsync(_cancellationToken));
        }
    }
}
