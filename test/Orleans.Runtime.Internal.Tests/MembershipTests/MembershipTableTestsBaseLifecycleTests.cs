using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans;
using Orleans.Clustering.TestKit;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.MembershipTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Membership")]
[TestCategory("BVT"), TestCategory("Membership")]
public sealed class MembershipTableTestsBaseLifecycleTests
{
    [Fact]
    public async Task ConstructionAndXunitInitialization_DoNotCreateLegacyResources()
    {
        var events = new List<string>();
        var fixture = CreateFixture(events);

        await fixture.InitializeAsync();
        await fixture.DisposeAsync();
        await fixture.DisposeAsync();

        Assert.Empty(events);
    }

    [Fact]
    public async Task MembershipInitialization_IsSharedAndDoesNotCreateGateway()
    {
        var events = new List<string>();
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = CreateFixture(events, initializeTable: _ => initialized.Task);

        var first = fixture.GetTableAsync(TestContext.Current.CancellationToken);
        var second = fixture.GetTableAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Same(first, second);
            Assert.Equal(["create-table", "initialize-table"], events);
        }
        finally
        {
            initialized.SetResult();
            await first;
            await fixture.DisposeAsync();
        }

        Assert.Equal(["create-table", "initialize-table", "delete-table", "dispose-table"], events);
    }

    [Fact]
    public async Task GatewayInitialization_UsesLegacyTableAndDisposesInOrder()
    {
        var events = new List<string>();
        var fixture = CreateFixture(events);

        await fixture.GetGatewayAsync(TestContext.Current.CancellationToken);
        await fixture.DisposeAsync();
        await fixture.DisposeAsync();

        Assert.Equal(
            ["create-table", "initialize-table", "create-gateway", "initialize-gateway",
                "delete-table", "dispose-gateway", "dispose-table"],
            events);
    }

    [Fact]
    public async Task MembershipInitializationFailure_DisposesOwnerWithoutConstructingGateway()
    {
        var events = new List<string>();
        var failure = new InvalidOperationException("membership initialization");
        var fixture = CreateFixture(events, initializeTable: _ => Task.FromException(failure));

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.InitializeTableAsync(TestContext.Current.CancellationToken));
        Assert.Same(failure, observed);
        await fixture.DisposeAsync();

        Assert.Equal(["create-table", "initialize-table", "dispose-table"], events);
    }

    [Fact]
    public async Task GatewayInitializationFailure_DisposesGatewayThenCleansMembership()
    {
        var events = new List<string>();
        var failure = new InvalidOperationException("gateway initialization");
        var fixture = CreateFixture(events, initializeGateway: () => Task.FromException(failure));

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.GetGatewayAsync(TestContext.Current.CancellationToken));
        Assert.Same(failure, observed);
        await fixture.DisposeAsync();

        Assert.Equal(
            ["create-table", "initialize-table", "create-gateway", "initialize-gateway",
                "dispose-gateway", "delete-table", "dispose-table"],
            events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposal_JoinsPendingInitializationBeforeDeletingAndClosing(bool pendingGateway)
    {
        var events = new List<string>();
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = CreateFixture(
            events,
            initializeTable: _ => pendingGateway ? Task.CompletedTask : initialized.Task,
            initializeGateway: () => pendingGateway ? initialized.Task : Task.CompletedTask);
        var initialization = fixture.GetGatewayAsync(TestContext.Current.CancellationToken);
        var disposal = fixture.DisposeAsync().AsTask();
        try
        {
            Assert.False(disposal.IsCompleted);
            Assert.DoesNotContain("delete-table", events);
            Assert.DoesNotContain("dispose-table", events);
            Assert.DoesNotContain("dispose-gateway", events);
        }
        finally
        {
            initialized.SetResult();
            await initialization;
            await disposal;
        }

        Assert.Equal(
            ["create-table", "initialize-table", "create-gateway", "initialize-gateway",
                "delete-table", "dispose-gateway", "dispose-table"],
            events);
    }

    [Fact]
    public async Task DeletionFailure_StillDisposesGatewayAndTable()
    {
        var events = new List<string>();
        var failure = new InvalidOperationException("membership deletion");
        var fixture = CreateFixture(events, deleteTable: _ => Task.FromException(failure));
        await fixture.GetGatewayAsync(TestContext.Current.CancellationToken);

        var observed = await Assert.ThrowsAsync<AggregateException>(() => fixture.DisposeAsync().AsTask());

        Assert.Same(failure, Assert.Single(observed.InnerExceptions));
        Assert.Equal(
            ["create-table", "initialize-table", "create-gateway", "initialize-gateway",
                "delete-table", "dispose-gateway", "dispose-table"],
            events);
    }

    [Fact]
    public async Task CanceledInitialization_DoesNotCreateLegacyResources()
    {
        var events = new List<string>();
        var fixture = CreateFixture(events);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.InitializeTableAsync(cancellation.Token));
        await fixture.DisposeAsync();

        Assert.Empty(events);
    }

    [Fact]
    public async Task InitializationCancellation_AwaitsReturnedNativeTaskBeforeDisposing()
    {
        var events = new List<string>();
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken nativeToken = default;
        var fixture = CreateFixture(events, initializeTable: token =>
        {
            nativeToken = token;
            return initialized.Task;
        });
        using var cancellation = new CancellationTokenSource();
        var initialization = fixture.InitializeTableAsync(cancellation.Token);
        cancellation.Cancel();
        try
        {
            Assert.True(nativeToken.IsCancellationRequested);
            Assert.False(initialization.IsCompleted);
            Assert.Equal(["create-table", "initialize-table"], events);
        }
        finally
        {
            initialized.SetCanceled(nativeToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
            await fixture.DisposeAsync();
        }

        Assert.Equal(["create-table", "initialize-table", "dispose-table"], events);
    }

    [Fact]
    public async Task LegacyNamedRowScenario_UsesOnlyFullSnapshotsWithinOwnerLifetime()
    {
        var events = new List<string>();
        var fixture = CreateFixture(events);
        var token = TestContext.Current.CancellationToken;
        var table = await fixture.GetTableAsync(token);
        var data = new MembershipTableData(new TableVersion(0, "initial"));
        MembershipEntry? committed = null;
        table.ReadAllAsync(Arg.Any<CancellationToken>()).Returns(_ => data);
        table.InsertRowAsync(Arg.Any<MembershipEntry>(), Arg.Any<TableVersion>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (committed is not null)
                {
                    return false;
                }

                committed = call.ArgAt<MembershipEntry>(0).Copy();
                var version = call.ArgAt<TableVersion>(1);
                Assert.Equal(new TableVersion(1, "initial"), version);
                data = new MembershipTableData(Tuple.Create(committed, "committed-row"), new TableVersion(1, "committed-table"));
                return true;
            });
        table.ClearReceivedCalls();
        try
        {
            await fixture.RunLegacyNamedRowScenarioAsync();

            Assert.Equal(new TableVersion(1, "committed-table"), data.Version);
            Assert.Equal("committed-row", Assert.Single(data.Members).Item2);
            var calls = table.ReceivedCalls().ToArray();
            Assert.Equal(4, calls.Count(call => call.GetMethodInfo().Name == nameof(IMembershipTable.ReadAllAsync)));
            Assert.Equal(4, calls.Count(call => call.GetMethodInfo().Name == nameof(IMembershipTable.InsertRowAsync)));
            Assert.All(calls, call => Assert.Contains(call.GetMethodInfo().Name,
                new[] { nameof(IMembershipTable.ReadAllAsync), nameof(IMembershipTable.InsertRowAsync) }));
            Assert.Equal(["create-table", "initialize-table"], events);
        }
        finally
        {
            await fixture.DisposeAsync();
        }

        Assert.Equal(["create-table", "initialize-table", "delete-table", "dispose-table"], events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeReceiptScenario_UsesReceiptTokensAndPreservesOwnerLifetime(bool inspectInsertSnapshot)
    {
        var events = new List<string>();
        var fixture = CreateFixture(events);
        var token = TestContext.Current.CancellationToken;
        var table = await fixture.GetTableAsync(token);
        var initialVersion = new TableVersion(0, "native-table-0");
        var insertReceipt = new MembershipTableWriteReceipt(new TableVersion(1, "native-table-1"), "native-row-1");
        var updateReceipt = new MembershipTableWriteReceipt(new TableVersion(2, "native-table-2"), "native-row-2");
        MembershipEntry? insertedEntry = null;
        MembershipEntry? updatedEntry = null;
        var reads = 0;
        var updates = 0;
        table.ReadAllAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (++reads == 1)
            {
                return new MembershipTableData(initialVersion);
            }

            if (updatedEntry is { } updated)
            {
                return new MembershipTableData(Tuple.Create(updated.Copy(), updateReceipt.RowETag), updateReceipt.Version);
            }

            var inserted = Assert.IsType<MembershipEntry>(insertedEntry);
            return new MembershipTableData(Tuple.Create(inserted.Copy(), insertReceipt.RowETag), insertReceipt.Version);
        });
        table.InsertRowWithResultAsync(Arg.Any<MembershipEntry>(), Arg.Any<TableVersion>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Assert.Equal(initialVersion.Next(), call.ArgAt<TableVersion>(1));
                insertedEntry = call.ArgAt<MembershipEntry>(0).Copy();
                Assert.Equal("receipt-host", insertedEntry.HostName);
                Assert.Equal("receipt-silo", insertedEntry.SiloName);
                Assert.Equal(SiloStatus.Joining, insertedEntry.Status);
                Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), insertedEntry.StartTime);
                Assert.Equal(insertedEntry.StartTime.AddMinutes(1), insertedEntry.IAmAliveTime);
                Assert.Empty(insertedEntry.SuspectTimes!);
                return new MembershipTableWriteResult(true, insertReceipt);
            });
        table.UpdateRowWithResultAsync(Arg.Any<MembershipEntry>(), Arg.Any<string>(), Arg.Any<TableVersion>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Assert.Equal(insertReceipt.RowETag, call.ArgAt<string>(1));
                Assert.Equal(insertReceipt.Version.Next(), call.ArgAt<TableVersion>(2));
                var input = call.ArgAt<MembershipEntry>(0);
                if (++updates == 1)
                {
                    Assert.Equal(SiloStatus.Active, input.Status);
                    updatedEntry = input.Copy();
                    return new MembershipTableWriteResult(true, updateReceipt);
                }

                Assert.Equal(SiloStatus.ShuttingDown, input.Status);
                return default(MembershipTableWriteResult);
            });
        table.ClearReceivedCalls();
        try
        {
            await fixture.RunNativeReceiptScenarioAsync(inspectInsertSnapshot);

            var expectedCalls = new List<string> { nameof(IMembershipTable.ReadAllAsync), nameof(IMembershipTable.InsertRowWithResultAsync) };
            if (inspectInsertSnapshot)
            {
                expectedCalls.Add(nameof(IMembershipTable.ReadAllAsync));
            }

            expectedCalls.AddRange([
                nameof(IMembershipTable.UpdateRowWithResultAsync), nameof(IMembershipTable.ReadAllAsync),
                nameof(IMembershipTable.UpdateRowWithResultAsync), nameof(IMembershipTable.ReadAllAsync)]);
            var calls = table.ReceivedCalls().ToArray();
            Assert.Equal(expectedCalls, calls.Select(call => call.GetMethodInfo().Name));
            Assert.All(calls, call => Assert.Equal(token, Assert.IsType<CancellationToken>(call.GetArguments()[^1])));
            Assert.Equal(inspectInsertSnapshot ? 4 : 3, reads);
            Assert.Equal(2, updates);
            var stored = Assert.IsType<MembershipEntry>(updatedEntry);
            var expected = Assert.IsType<MembershipEntry>(insertedEntry).Copy();
            expected.Status = SiloStatus.Active;
            Assert.Equal(expected.ToFullString(), stored.ToFullString());
            Assert.Equal(new TableVersion(1, "native-table-1"), insertReceipt.Version);
            Assert.Equal("native-row-1", insertReceipt.RowETag);
            Assert.Equal(["create-table", "initialize-table"], events);
        }
        finally
        {
            await fixture.DisposeAsync();
        }

        Assert.Equal(["create-table", "initialize-table", "delete-table", "dispose-table"], events);
    }

    private static LegacyFixtureHarness CreateFixture(
        List<string> events,
        Func<CancellationToken, Task>? initializeTable = null,
        Func<Task>? initializeGateway = null,
        Func<CancellationToken, Task>? deleteTable = null)
    {
        var table = Substitute.For<IMembershipTable, IAsyncDisposable>();
        table.InitializeMembershipTableAsync(true, Arg.Any<CancellationToken>()).Returns(call =>
        {
            events.Add("initialize-table");
            return initializeTable?.Invoke(call.ArgAt<CancellationToken>(1)) ?? Task.CompletedTask;
        });
        table.DeleteMembershipTableEntriesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            events.Add("delete-table");
            return deleteTable?.Invoke(call.ArgAt<CancellationToken>(1)) ?? Task.CompletedTask;
        });
        ((IAsyncDisposable)table).DisposeAsync().Returns(_ =>
        {
            events.Add("dispose-table");
            return ValueTask.CompletedTask;
        });

        var gateway = Substitute.For<IGatewayListProvider, IDisposable>();
        gateway.InitializeGatewayListProvider().Returns(_ =>
        {
            events.Add("initialize-gateway");
            return initializeGateway?.Invoke() ?? Task.CompletedTask;
        });
        ((IDisposable)gateway).When(value => value.Dispose()).Do(_ => events.Add("dispose-gateway"));
        return Substitute.ForPartsOf<LegacyFixtureHarness>(table, gateway, events);
    }

    // Only a runtime proxy is concrete, so the inherited conformance facts are not discoverable test cases.
    public abstract class LegacyFixtureHarness(
        IMembershipTable table, IGatewayListProvider gateway, List<string> events)
        : MembershipTableTestsBase(new ConnectionStringFixture(), null!, new LoggerFilterOptions())
    {
        public Task InitializeTableAsync(CancellationToken cancellationToken)
            => InitializeLegacyMembershipTableAsync(cancellationToken);

        public Task<IMembershipTable> GetTableAsync(CancellationToken cancellationToken)
            => GetLegacyMembershipTableAsync(cancellationToken);

        public Task<IGatewayListProvider> GetGatewayAsync(CancellationToken cancellationToken)
            => GetLegacyGatewayListProviderAsync(cancellationToken);

        public Task RunLegacyNamedRowScenarioAsync() => MembershipTable_ReadRow_Insert_Read();

        public Task RunNativeReceiptScenarioAsync(bool inspectInsertSnapshot)
            => MembershipTable_NativeReceiptProvenance(inspectInsertSnapshot);

        protected override Task<string> GetConnectionString() => Task.FromResult("lifecycle-test");

        protected override IMembershipTable CreateMembershipTable(ILogger logger)
        {
            events.Add("create-table");
            return table;
        }

        protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger, IMembershipTable membershipTable)
        {
            Assert.Same(table, membershipTable);
            events.Add("create-gateway");
            return gateway;
        }

        protected override IMembershipTable CreateMembershipTable(ILogger logger, IOptions<ClusterOptions> clusterOptions)
            => throw new NotSupportedException("This harness does not execute conformance cases.");

        protected override IGatewayListProvider CreateGatewayListProvider(ILogger logger)
            => throw new NotSupportedException("Use the gateway overload receiving the legacy table.");

        protected override MembershipTableTestFixture CreateConformanceFixture()
            => throw new NotSupportedException("This harness does not execute conformance cases.");
    }
}
