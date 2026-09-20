using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Membership;
using org.apache.zookeeper;
using Polly;
using TestExtensions;
using Xunit;

namespace UnitTests.MembershipTests;

[TestCategory("Membership"), TestCategory("ZooKeeper")]
[TestSuite("BVT"), TestProvider("ZooKeeper"), TestArea("Membership")]
public sealed class ZooKeeperReadRetryTests
{
    [Theory]
    [InlineData("sync", false)]
    [InlineData("children", false)]
    [InlineData("before", true)]
    [InlineData("member", false)]
    [InlineData("heartbeat", false)]
    [InlineData("after", false)]
    public async Task Read_ConnectionLossThenSuccess_RetriesOnlyFailedNativeRequest(string boundary, bool point)
    {
        var harness = await Harness.CreateAsync();
        var key = boundary switch
        {
            "sync" => "Sync /",
            "children" => "GetChildren /",
            "before" or "after" => "GetData /",
            "member" => "GetData " + ZooKeeperNativeFake.RowPath(harness.Entries[0].SiloAddress),
            "heartbeat" => "GetData " + ZooKeeperNativeFake.HeartbeatPath(harness.Entries[0].SiloAddress),
            _ => throw new ArgumentOutOfRangeException(nameof(boundary))
        };
        var failure = new KeeperException.ConnectionLossException();
        var failed = false;
        harness.BeforeRequest = request =>
        {
            if (request == key && !failed)
            {
                failed = true;
                throw failure;
            }
            return Task.CompletedTask;
        };

        var read = harness.Read(point, TestContext.Current.CancellationToken);
        await harness.Clock.AdvanceNextAsync(TimeSpan.FromMilliseconds(250));
        var result = await read;

        harness.AssertSnapshot(result, point ? [harness.Entries[0]] : harness.Entries, 2);
        var expected = harness.ExpectedReadCalls(point).GroupBy(value => value).ToDictionary(group => group.Key, group => group.Count());
        expected[key]++;
        Assert.Equal(expected.OrderBy(pair => pair.Key), harness.CountCalls().OrderBy(pair => pair.Key));
        var warning = Assert.Single(harness.Logger.Warnings);
        Assert.Same(failure, warning.Exception);
        Assert.Equal(key.Split(' ')[0], warning.Values["Operation"]);
        Assert.Equal(1, warning.Values["Retry"]);
        Assert.Equal(250d, warning.Values["DelayMilliseconds"]);
        harness.AssertOneOwner(readOnly: true);
    }

    [Fact]
    public async Task ReadRetry_Exhaustion_PreservesFinalExceptionAndBackoff()
    {
        var harness = await Harness.CreateAsync();
        var failures = Enumerable.Range(0, 5).Select(_ => new KeeperException.ConnectionLossException()).ToArray();
        var times = new List<DateTimeOffset>();
        harness.BeforeRequest = _ =>
        {
            times.Add(harness.Clock.GetUtcNow());
            throw failures[times.Count - 1];
        };
        var read = harness.Read(TestContext.Current.CancellationToken);
        var completion = Record.ExceptionAsync(() => read);
        foreach (var delay in new[] { 250, 500, 1000, 2000 })
        {
            var timer = await harness.Clock.NextTimerAsync();
            Assert.Equal(TimeSpan.FromMilliseconds(delay), timer);
            var attempts = harness.Calls.Count;
            harness.Clock.Advance(timer - TimeSpan.FromMilliseconds(1));
            Assert.Equal(attempts, harness.Calls.Count);
            harness.Clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.Same(failures[^1], await completion);
        Assert.Equal(new[] { 0d, 250d, 750d, 1750d, 3750d }, times.Select(time => (time - times[0]).TotalMilliseconds));
        Assert.Equal(Enumerable.Repeat("Sync /", 5), harness.Calls);
        Assert.Equal(failures.Take(4), harness.Logger.Warnings.Select(warning => warning.Exception));
        Assert.Equal(new[] { 1, 2, 3, 4 }, harness.Logger.Warnings.Select(warning => (int)warning.Values["Retry"]!));
        Assert.All(harness.Logger.Warnings, warning => Assert.Equal(4, warning.Values["MaxRetries"]));
        harness.AssertOneOwner(readOnly: true);
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("session")]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("cancellation")]
    [InlineData("ordinary")]
    public async Task ReadRetry_IneligibleFailure_PropagatesAfterOneAttempt(string kind)
    {
        var harness = await Harness.CreateAsync();
        Exception failure = kind switch
        {
            "authorization" => new KeeperException.NoAuthException(),
            "session" => new KeeperException.SessionExpiredException(),
            "missing" => new KeeperException.NoNodeException("/"),
            "version" => new KeeperException.BadVersionException("/"),
            "cancellation" => new OperationCanceledException(),
            "ordinary" => new InvalidOperationException("native failure"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        harness.BeforeRequest = _ => Task.FromException(failure);

        Assert.Same(failure, await Record.ExceptionAsync(() => harness.Read(TestContext.Current.CancellationToken)));
        Assert.Equal("Sync /", Assert.Single(harness.Calls));
        Assert.Empty(harness.Logger.Warnings);
        harness.AssertOneOwner(readOnly: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOwner_PreCanceled_DoesNotCreateSession(bool point)
    {
        var harness = await Harness.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Read(point, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Empty(harness.Sessions);
        Assert.Empty(harness.Calls);
        Assert.Equal(0, harness.CloseCount);
    }

    [Fact]
    public async Task ReadRetry_CancellationDuringDelay_StopsAdmission()
    {
        var harness = await Harness.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        harness.BeforeRequest = _ => Task.FromException(new KeeperException.ConnectionLossException());
        var read = harness.Read(cancellationToken: cancellation.Token);
        Assert.Equal(TimeSpan.FromMilliseconds(250), await harness.Clock.NextTimerAsync());

        cancellation.Cancel();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Assert.Single(harness.Sessions).Completion);
        harness.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal("Sync /", Assert.Single(harness.Calls));
        Assert.Single(harness.Logger.Warnings);
        harness.AssertOneOwner(readOnly: true);
    }

    [Fact]
    public async Task ReadOwner_CanceledCaller_JoinsNativeTasksBeforeClose()
    {
        var harness = await Harness.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var first = Gate();
        var second = Gate();
        var firstCompleted = Gate();
        var closeStarted = Gate();
        var releaseClose = Gate();
        var firstKey = "GetData " + ZooKeeperNativeFake.RowPath(harness.Entries[0].SiloAddress);
        var secondKey = "GetData " + ZooKeeperNativeFake.RowPath(harness.Entries[1].SiloAddress);
        harness.BeforeRequest = request => request == firstKey ? first.Task : request == secondKey ? second.Task : Task.CompletedTask;
        harness.AfterRequest = request =>
        {
            if (request == firstKey)
                firstCompleted.SetResult();
        };
        harness.Close = () =>
        {
            closeStarted.SetResult();
            return releaseClose.Task;
        };

        var read = harness.Read(cancellationToken: cancellation.Token);
        var owner = Assert.Single(harness.Sessions);
        try
        {
            Assert.Equal(new[] { "Sync /", "GetChildren /", firstKey, secondKey }, harness.Calls);
            cancellation.Cancel();
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.False(owner.Completion.IsCompleted);
            Assert.False(closeStarted.Task.IsCompleted);
            first.SetResult();
            await firstCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(owner.Completion.IsCompleted);
            Assert.False(closeStarted.Task.IsCompleted);
            second.SetResult();
            await closeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(owner.Completion.IsCompleted);
        }
        finally
        {
            first.TrySetResult();
            second.TrySetResult();
            releaseClose.TrySetResult();
            await Record.ExceptionAsync(() => owner.Completion);
        }

        var ownedFailure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.Completion);
        Assert.Equal(cancellation.Token, ownedFailure.CancellationToken);
        Assert.Equal(new[] { "Sync /", "GetChildren /", firstKey, secondKey }, harness.Calls);
        harness.AssertOneOwner(readOnly: true);
    }

    [Fact]
    public async Task ReadOwner_CloseCompletesBeforeResult()
    {
        var harness = await Harness.CreateAsync();
        var close = Gate();
        harness.Close = () => close.Task;
        var read = harness.Read(TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(harness.ExpectedReadCalls(), harness.Calls);
            Assert.Equal(1, harness.CloseCount);
            Assert.False(read.IsCompleted);
            Assert.False(Assert.Single(harness.Sessions).Completion.IsCompleted);
        }
        finally
        {
            close.TrySetResult();
        }
        harness.AssertSnapshot(await read, harness.Entries, 2);
        harness.AssertOneOwner(readOnly: true);
    }

    [Fact]
    public async Task ReadOwner_CloseFailureAfterSuccess_DoesNotReplay()
    {
        var harness = await Harness.CreateAsync();
        var failure = new KeeperException.ConnectionLossException();
        harness.Close = () => Task.FromException(failure);

        Assert.Same(failure, await Record.ExceptionAsync(() => harness.Read(TestContext.Current.CancellationToken)));

        Assert.Equal(harness.ExpectedReadCalls(), harness.Calls);
        Assert.Empty(harness.Logger.Warnings);
        harness.AssertOneOwner(readOnly: true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Read_ConnectionLossDuringConcurrentMutation_RefencesWholeSnapshot(bool point, bool cleanup)
    {
        var harness = await Harness.CreateAsync();
        var entry = harness.Entries[0];
        var heartbeat = "GetData " + ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress);
        var failed = false;
        harness.BeforeRequest = request =>
        {
            if (request == heartbeat && !failed)
            {
                failed = true;
                throw new KeeperException.ConnectionLossException();
            }
            return Task.CompletedTask;
        };
        var read = harness.Read(point, TestContext.Current.CancellationToken);
        var delay = await harness.Clock.NextTimerAsync();
        if (cleanup)
        {
            harness.Fake.Nodes.Remove(ZooKeeperNativeFake.RowPath(entry.SiloAddress));
            harness.Fake.Nodes.Remove(ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress));
            var root = harness.Fake.Nodes["/"];
            harness.Fake.Nodes["/"] = root with { ChildrenVersion = root.ChildrenVersion + 1 };
        }
        else
        {
            entry.Status = SiloStatus.Dead;
            Assert.True(await ZooKeeperBasedMembershipTable.UpdateRowCoreAsync(
                harness.Fake.Operations, entry, "0", new TableVersion(3, "2"), TestContext.Current.CancellationToken));
        }
        harness.Clock.Advance(delay);

        var result = await read;
        var entries = cleanup ? harness.Entries.Skip(1).ToArray() : harness.Entries;
        harness.AssertSnapshot(result, point ? entries.Where(value => value.SiloAddress.Equals(entry.SiloAddress)) : entries,
            cleanup ? 2 : 3);
        Assert.Equal(1, harness.Calls.Count(call => call == "Sync /"));
        Assert.Equal(point ? 4 : 2, harness.Calls.Count(call => call == (point ? "GetData /" : "GetChildren /")));
        Assert.Equal(cleanup && !point ? 1 : 2,
            harness.Calls.Count(call => call == "GetData " + ZooKeeperNativeFake.RowPath(entry.SiloAddress)));
        harness.AssertOneOwner(readOnly: true);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(128)]
    public async Task Read_RetryingOneRow_PreservesSuccessfulSiblings(int rowCount)
    {
        var harness = await Harness.CreateAsync(rowCount);
        var key = "GetData " + ZooKeeperNativeFake.RowPath(harness.Entries[0].SiloAddress);
        var failed = false;
        harness.BeforeRequest = request =>
        {
            if (request == key && !failed)
            {
                failed = true;
                throw new KeeperException.ConnectionLossException();
            }
            return Task.CompletedTask;
        };
        var read = harness.Read(TestContext.Current.CancellationToken);
        Assert.False(read.IsCompleted);
        Assert.All(harness.Entries.Skip(1), entry =>
            Assert.Contains("GetData " + ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress), harness.Calls));
        await harness.Clock.AdvanceNextAsync(TimeSpan.FromMilliseconds(250));

        harness.AssertSnapshot(await read, harness.Entries, rowCount);
        var expected = harness.ExpectedReadCalls().ToDictionary(call => call, _ => 1);
        expected[key]++;
        Assert.Equal(expected.OrderBy(pair => pair.Key), harness.CountCalls().OrderBy(pair => pair.Key));
        harness.AssertOneOwner(readOnly: true);
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(9, true)]
    [InlineData(128, false)]
    [InlineData(128, true)]
    public async Task ReadOwner_StartsEveryRowBeforeAwaitingNativeCompletion(int rowCount, bool cancel)
    {
        var harness = await Harness.CreateAsync(rowCount);
        using var cancellation = new CancellationTokenSource();
        var release = Gate();
        harness.BeforeRequest = request => request.StartsWith("GetData ", StringComparison.Ordinal) && request != "GetData /"
            && !request.EndsWith("/IAmAlive", StringComparison.Ordinal) ? release.Task : Task.CompletedTask;
        var read = harness.Read(cancellationToken: cancellation.Token);
        var owner = Assert.Single(harness.Sessions);
        try
        {
            Assert.Equal(new[] { "Sync /", "GetChildren /" }.Concat(
                harness.Entries.Select(entry => "GetData " + ZooKeeperNativeFake.RowPath(entry.SiloAddress))), harness.Calls);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            }
            Assert.False(owner.Completion.IsCompleted);
            Assert.Equal(0, harness.CloseCount);
        }
        finally
        {
            release.TrySetResult();
            await Record.ExceptionAsync(() => owner.Completion);
        }
        if (cancel)
        {
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.Completion);
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.Equal(2 + rowCount, harness.Calls.Count);
        }
        else
        {
            harness.AssertSnapshot(await read, harness.Entries, rowCount);
            Assert.Equal(3 + rowCount * 2, harness.Calls.Count);
        }
        harness.AssertOneOwner(readOnly: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_MissingRow_PreservesOtherNativeFailureAfterJoining(bool connectionLoss)
    {
        var harness = await Harness.CreateAsync();
        var release = Gate();
        Exception failure = connectionLoss ? new KeeperException.ConnectionLossException() : new KeeperException.NoAuthException();
        var first = "GetData " + ZooKeeperNativeFake.RowPath(harness.Entries[0].SiloAddress);
        var second = "GetData " + ZooKeeperNativeFake.RowPath(harness.Entries[1].SiloAddress);
        harness.BeforeRequest = async request =>
        {
            if (request == first)
                throw new KeeperException.NoNodeException(ZooKeeperNativeFake.RowPath(harness.Entries[0].SiloAddress));
            if (request == second)
            {
                await release.Task;
                throw failure;
            }
        };
        var read = harness.Read(TestContext.Current.CancellationToken);
        var completion = Record.ExceptionAsync(() => read);
        try
        {
            Assert.Contains(second, harness.Calls);
            Assert.False(read.IsCompleted);
            Assert.Equal(0, harness.CloseCount);
        }
        finally
        {
            release.TrySetResult();
        }
        if (connectionLoss)
        {
            foreach (var delay in new[] { 250, 500, 1000, 2000 })
                await harness.Clock.AdvanceNextAsync(TimeSpan.FromMilliseconds(delay));
        }
        Assert.Same(failure, await completion);
        Assert.DoesNotContain("GetData /", harness.Calls);
        harness.AssertOneOwner(readOnly: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetGateways_ConnectionLoss_UsesRetriedSnapshot(bool exhaust)
    {
        var harness = await Harness.CreateAsync(3);
        harness.Entries[1].ProxyPort = 0;
        harness.Entries[2].Status = SiloStatus.Dead;
        foreach (var entry in harness.Entries)
        {
            var path = ZooKeeperNativeFake.RowPath(entry.SiloAddress);
            harness.Fake.Nodes[path] = harness.Fake.Nodes[path] with { Data = ZooKeeperBasedMembershipTable.Serialize(entry) };
        }
        var failure = new KeeperException.ConnectionLossException();
        var failures = 0;
        harness.BeforeRequest = request =>
        {
            if (request == "Sync /" && (exhaust || failures++ == 0))
                throw failure;
            return Task.CompletedTask;
        };
        var provider = new ZooKeeperGatewayListProvider(
            NullLogger<ZooKeeperGatewayListProvider>.Instance,
            Options.Create(new ZooKeeperGatewayListProviderOptions { ConnectionString = "unused.invalid" }),
            Options.Create(new GatewayOptions()),
            Options.Create(new ClusterOptions { ClusterId = "test" }),
            () => harness.CreateSession(true),
            harness.Pipeline);
        var read = provider.GetGateways();
        var completion = Record.ExceptionAsync(() => read);
        foreach (var delay in exhaust ? new[] { 250, 500, 1000, 2000 } : [250])
            await harness.Clock.AdvanceNextAsync(TimeSpan.FromMilliseconds(delay));
        if (exhaust)
        {
            Assert.Same(failure, await completion);
        }
        else
        {
            Assert.Null(await completion);
            var entry = harness.Entries[0];
            Assert.Equal(SiloAddress.New(entry.SiloAddress.Endpoint.Address, entry.ProxyPort, entry.SiloAddress.Generation).ToGatewayUri(),
                Assert.Single(await read));
        }
        harness.AssertOneOwner(readOnly: true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConditionalWrite_CommitOrCloseLoss_PropagatesWithoutReplay(bool update, bool closeFailure)
    {
        var harness = await Harness.CreateAsync(1);
        var entry = update ? harness.Entries[0] : Harness.Entry(1);
        entry.Status = SiloStatus.Dead;
        var failure = new KeeperException.ConnectionLossException();
        if (closeFailure)
            harness.Close = () => Task.FromException(failure);
        else
            harness.AfterMulti = () => Task.FromException(failure);

        var actual = await Record.ExceptionAsync(() => update
            ? harness.Table.UpdateRowAsync(entry, "0", new TableVersion(2, "1"), TestContext.Current.CancellationToken)
            : harness.Table.InsertRowAsync(entry, new TableVersion(2, "1"), TestContext.Current.CancellationToken));

        Assert.Same(failure, actual);
        Assert.Equal("Multi", Assert.Single(harness.Calls));
        Assert.Single(harness.Fake.Transactions);
        Assert.Equal(2, harness.Fake.Nodes["/"].Version);
        var row = harness.Fake.Nodes[ZooKeeperNativeFake.RowPath(entry.SiloAddress)];
        Assert.Equal(update ? 1 : 0, row.Version);
        Assert.Equal(ZooKeeperBasedMembershipTable.Serialize(entry), row.Data);
        Assert.Equal(update ? 3 : 5, harness.Fake.Nodes.Count);
        Assert.Empty(harness.Logger.Warnings);
        harness.AssertOneOwner(readOnly: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionalWrite_KnownConflict_PreservesFalseResult(bool update)
    {
        var harness = await Harness.CreateAsync(1);
        var before = harness.Fake.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value);
        var result = update
            ? await harness.Table.UpdateRowAsync(harness.Entries[0], "99", new TableVersion(2, "1"), TestContext.Current.CancellationToken)
            : await harness.Table.InsertRowAsync(harness.Entries[0], new TableVersion(2, "1"), TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal(before, harness.Fake.Nodes);
        Assert.Equal("Multi", Assert.Single(harness.Calls));
        Assert.Empty(harness.Logger.Warnings);
        harness.AssertOneOwner(readOnly: false);
    }

    [Fact]
    public async Task ReadDecorator_PreservesMutationDelegates()
    {
        var harness = await Harness.CreateAsync();
        var native = harness.Fake.Operations;
        var wrapped = ZooKeeperReadRetryPolicy.Wrap(native, harness.Pipeline, CancellationToken.None);

        Assert.Same(native.Multi, wrapped.Multi);
        Assert.Same(native.SetData, wrapped.SetData);
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Harness
    {
        internal ZooKeeperNativeFake Fake { get; } = new();
        internal RetryClock Clock { get; } = new();
        internal RecordingLogger Logger { get; } = new();
        internal ConcurrentQueue<string> Calls { get; } = new();
        internal List<ZooKeeperSession> Sessions { get; } = [];
        internal List<bool> ReadOnly { get; } = [];
        internal Func<string, Task>? BeforeRequest { get; set; }
        internal Action<string>? AfterRequest { get; set; }
        internal Func<Task> Close { get; set; } = () => Task.CompletedTask;
        internal Func<Task> AfterMulti { get; set; } = () => Task.CompletedTask;
        internal int CloseCount;
        internal MembershipEntry[] Entries { get; private set; } = [];
        internal ResiliencePipeline Pipeline { get; }
        internal ZooKeeperBasedMembershipTable Table { get; }

        private Harness()
        {
            Pipeline = ZooKeeperReadRetryPolicy.CreatePipeline(Logger, Clock);
            Table = new ZooKeeperBasedMembershipTable(NullLogger<ZooKeeperBasedMembershipTable>.Instance,
                Options.Create(new ZooKeeperClusteringSiloOptions { ConnectionString = "unused.invalid" }),
                Options.Create(new ClusterOptions { ClusterId = "test" }), CreateSession, Pipeline);
        }

        internal static async Task<Harness> CreateAsync(int rowCount = 2)
        {
            var result = new Harness { Entries = Enumerable.Range(0, rowCount).Select(Entry).ToArray() };
            for (var index = 0; index < rowCount; index++)
            {
                Assert.True(await ZooKeeperBasedMembershipTable.InsertRowCoreAsync(result.Fake.Operations,
                    result.Entries[index], new TableVersion(index + 1, index.ToString(CultureInfo.InvariantCulture)),
                    TestContext.Current.CancellationToken));
            }
            result.Fake.Calls.Clear();
            result.Fake.Transactions.Clear();
            return result;
        }

        internal static MembershipEntry Entry(int index) => new()
        {
            SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 12345 + index),
            HostName = "host-a",
            SiloName = "silo-a",
            Status = SiloStatus.Active,
            ProxyPort = 30000 + index,
            StartTime = DateTime.UnixEpoch,
            IAmAliveTime = DateTime.UnixEpoch.AddDays(1)
        };

        internal ZooKeeperSession CreateSession(bool readOnly)
        {
            ReadOnly.Add(readOnly);
            var native = Fake.Operations;
            var operations = new ZooKeeperBasedMembershipTable.NativeOperations(
                path => Request("GetData " + path, () => native.GetData(path)),
                path => Request("GetChildren " + path, () => native.GetChildren(path)),
                path => Request("Sync " + path, async () => { await native.Sync(path); return true; }),
                async ops =>
                {
                    Calls.Enqueue("Multi");
                    await native.Multi(ops);
                    await AfterMulti();
                },
                native.SetData);
            var session = new ZooKeeperSession(operations, () =>
            {
                Interlocked.Increment(ref CloseCount);
                return Close();
            });
            Sessions.Add(session);
            return session;
        }

        private async Task<T> Request<T>(string request, Func<Task<T>> action)
        {
            Calls.Enqueue(request);
            if (BeforeRequest is { } before)
                await before(request);
            Task<T> native;
            lock (Fake.Calls)
                native = action();
            var result = await native;
            AfterRequest?.Invoke(request);
            return result;
        }

        internal Task<MembershipTableData> Read(CancellationToken cancellationToken) => Read(false, cancellationToken);

        internal Task<MembershipTableData> Read(bool point, CancellationToken cancellationToken) =>
            point ? Table.ReadRowAsync(Entries[0].SiloAddress, cancellationToken) : Table.ReadAllAsync(cancellationToken);

        internal IEnumerable<string> ExpectedReadCalls(bool point = false)
        {
            yield return "Sync /";
            yield return point ? "GetData /" : "GetChildren /";
            foreach (var entry in point ? Entries.Take(1) : Entries)
            {
                yield return "GetData " + ZooKeeperNativeFake.RowPath(entry.SiloAddress);
                yield return "GetData " + ZooKeeperNativeFake.HeartbeatPath(entry.SiloAddress);
            }
            yield return "GetData /";
        }

        internal Dictionary<string, int> CountCalls() =>
            Calls.GroupBy(value => value).ToDictionary(group => group.Key, group => group.Count());

        internal void AssertSnapshot(MembershipTableData snapshot, IEnumerable<MembershipEntry> expected, int version)
        {
            Assert.Equal(version, snapshot.Version.Version);
            Assert.Equal(version.ToString(CultureInfo.InvariantCulture), snapshot.Version.VersionEtag);
            var entries = expected.ToDictionary(entry => entry.SiloAddress);
            var actual = snapshot.Members.ToDictionary(row => row.Item1.SiloAddress);
            Assert.Equal(entries.Count, actual.Count);
            foreach (var (address, entry) in entries)
            {
                var row = actual[address];
                Assert.Equal(Fake.Nodes[ZooKeeperNativeFake.RowPath(address)].Version.ToString(CultureInfo.InvariantCulture), row.Item2);
                Assert.Equal(ZooKeeperBasedMembershipTable.Serialize(entry), ZooKeeperBasedMembershipTable.Serialize(row.Item1));
            }
        }

        internal void AssertOneOwner(bool readOnly)
        {
            Assert.Single(Sessions);
            Assert.Equal(readOnly, Assert.Single(ReadOnly));
            Assert.Equal(1, CloseCount);
            Assert.True(Sessions[0].Completion.IsCompleted);
        }
    }

    private sealed class RetryClock : TimeProvider
    {
        private readonly FakeTimeProvider _time = new();
        private readonly Channel<TimeSpan> _timers = Channel.CreateUnbounded<TimeSpan>();
        public override DateTimeOffset GetUtcNow() => _time.GetUtcNow();
        public override long GetTimestamp() => _time.GetTimestamp();
        public override long TimestampFrequency => _time.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _time.CreateTimer(callback, state, dueTime, period);
            Assert.True(_timers.Writer.TryWrite(dueTime));
            return timer;
        }

        internal ValueTask<TimeSpan> NextTimerAsync() => _timers.Reader.ReadAsync(TestContext.Current.CancellationToken);
        internal void Advance(TimeSpan duration) => _time.Advance(duration);
        internal async Task AdvanceNextAsync(TimeSpan expected)
        {
            Assert.Equal(expected, await NextTimerAsync());
            Advance(expected);
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        internal sealed record Warning(Exception? Exception, IReadOnlyDictionary<string, object?> Values);
        internal ConcurrentQueue<Warning> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Equal(LogLevel.Warning, logLevel);
            var values = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state);
            Warnings.Enqueue(new(exception, values.ToDictionary(pair => pair.Key, pair => pair.Value)));
        }
    }
}
