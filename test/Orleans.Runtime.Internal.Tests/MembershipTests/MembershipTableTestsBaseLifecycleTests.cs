using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans;
using Orleans.Clustering.TestKit;
using Orleans.Configuration;
using Orleans.Messaging;
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
