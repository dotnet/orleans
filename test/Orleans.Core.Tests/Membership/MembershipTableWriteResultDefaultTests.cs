using Orleans;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace NonSilo.Tests.Membership;

[TestCategory("BVT"), TestCategory("Membership")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class MembershipTableWriteResultDefaultTests
{
    [Theory]
    [InlineData("Insert", false, false)]
    [InlineData("Insert", false, true)]
    [InlineData("Insert", true, false)]
    [InlineData("Insert", true, true)]
    [InlineData("Update", false, false)]
    [InlineData("Update", false, true)]
    [InlineData("Update", true, false)]
    [InlineData("Update", true, true)]
    public async Task DefaultWriteResult_InvokesBoolOnceWithoutReading(string operation, bool native, bool succeeded)
    {
        LegacyBoolProvider provider = native ? new NativeBoolProvider() : new LegacyBoolProvider();
        provider.Write = _ => Task.FromResult(succeeded);
        using var cancellation = new CancellationTokenSource();

        var result = await Invoke(provider, operation, cancellation.Token);

        Assert.Equal(succeeded, result.Succeeded);
        Assert.Null(result.Receipt);
        AssertWrite(provider, operation, native, cancellation.Token);
    }

    [Theory]
    [InlineData("Insert", false)]
    [InlineData("Insert", true)]
    [InlineData("Update", false)]
    [InlineData("Update", true)]
    public async Task DefaultWriteResult_PreCanceled_DoesNotInvokeProvider(string operation, bool native)
    {
        LegacyBoolProvider provider = native ? new NativeBoolProvider() : new LegacyBoolProvider();
        provider.Write = _ => throw new InvalidOperationException("Cancellation must precede the provider call.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var pending = Invoke(provider, operation, cancellation.Token);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(pending.IsCanceled);
        Assert.Empty(provider.Calls);
        AssertNoReads(provider);
        Assert.False(provider.Started.Task.IsCompleted);
    }

    [Theory]
    [InlineData("Insert", false, false)]
    [InlineData("Insert", false, true)]
    [InlineData("Insert", true, false)]
    [InlineData("Insert", true, true)]
    [InlineData("Update", false, false)]
    [InlineData("Update", false, true)]
    [InlineData("Update", true, false)]
    [InlineData("Update", true, true)]
    public async Task DefaultWriteResult_PropagatesFailureWithoutRetry(string operation, bool native, bool synchronous)
    {
        LegacyBoolProvider provider = native ? new NativeBoolProvider() : new LegacyBoolProvider();
        var failure = new InvalidOperationException("The selected bool write failed.");
        provider.Write = _ => synchronous ? throw failure : Task.FromException<bool>(failure);
        using var cancellation = new CancellationTokenSource();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Invoke(provider, operation, cancellation.Token));

        Assert.Same(failure, exception);
        AssertWrite(provider, operation, native, cancellation.Token);
    }

    [Theory]
    [InlineData("Insert", false)]
    [InlineData("Insert", true)]
    [InlineData("Update", false)]
    [InlineData("Update", true)]
    public async Task DefaultWriteResult_CancellationRemainsVisible(string operation, bool native)
    {
        LegacyBoolProvider provider = native ? new NativeBoolProvider() : new LegacyBoolProvider();
        var work = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Write = native ? CompleteCooperatively : _ => work.Task;
        using var cancellation = new CancellationTokenSource();
        Task<MembershipTableWriteResult>? pending = null;

        try
        {
            pending = Invoke(provider, operation, cancellation.Token);
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            AssertWrite(provider, operation, native, cancellation.Token);
            Assert.False(pending.IsCompleted);
            Assert.False(work.Task.IsCompleted);

            cancellation.Cancel();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.True(pending.IsCanceled);
            if (native)
            {
                Assert.True(work.Task.IsCanceled);
                var providerException = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.Task);
                Assert.Equal(cancellation.Token, providerException.CancellationToken);
            }
            else
            {
                // The legacy adapter cancels only the caller's wait, not the owned tokenless work.
                Assert.False(work.Task.IsCompleted);
            }

            AssertWrite(provider, operation, native, cancellation.Token);
        }
        finally
        {
            // Settle owned work even when an assertion fails; do not leave a tokenless operation running.
            work.TrySetResult(false);
            if (pending is not null)
            {
                try
                {
                    await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
            }
        }

        async Task<bool> CompleteCooperatively(CancellationToken token)
        {
            // The native bool implementation owns cancellation, rather than requiring the rich DIM to wrap it.
            using var registration = token.Register(() => work.TrySetCanceled(token));
            return await work.Task;
        }
    }

    private static Task<MembershipTableWriteResult> Invoke(LegacyBoolProvider provider, string operation, CancellationToken token)
    {
        IMembershipTable table = provider;
        return operation switch
        {
            "Insert" => table.InsertRowWithResultAsync(provider.Entry, provider.Version, token),
            "Update" => table.UpdateRowWithResultAsync(provider.Entry, provider.RowETag, provider.Version, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    private static void AssertWrite(LegacyBoolProvider provider, string operation, bool native, CancellationToken token)
    {
        // A single total call also excludes alternate writes and retries, not just duplicate selected writes.
        var call = Assert.Single(provider.Calls);
        Assert.Equal($"{operation}Row{(native ? "Async" : "")}", call.Method);
        Assert.Equal(native ? token : CancellationToken.None, call.Token);
        Assert.Equal(operation == "Insert" ? 2 : 3, call.Arguments.Length);
        Assert.Same(provider.Entry, call.Arguments[0]);
        Assert.Same(provider.Version, call.Arguments[^1]);
        if (operation == "Update")
        {
            Assert.Equal(provider.RowETag, Assert.IsType<string>(call.Arguments[1]));
        }

        AssertNoReads(provider);
    }

    private static void AssertNoReads(LegacyBoolProvider provider)
    {
        Assert.Equal(0, provider.ReadRowCalls);
        Assert.Equal(0, provider.ReadAllCalls);
        Assert.Equal(0, provider.ReadRowAsyncCalls);
        Assert.Equal(0, provider.ReadAllAsyncCalls);
    }

    private class LegacyBoolProvider : IMembershipTable
    {
        public MembershipEntry Entry { get; } = new()
        {
            SiloAddress = SiloAddress.FromParsableString("127.0.0.1:11111@37"),
            Status = SiloStatus.Active,
            HostName = "membership-default-host",
            SiloName = "membership-default-silo",
            IAmAliveTime = DateTime.UnixEpoch.AddMinutes(12),
        };

        public TableVersion Version { get; } = new(42, "table/expected:\"opaque\"");
        public string RowETag { get; } = "row/expected:\u03BB";
        public Func<CancellationToken, Task<bool>> Write { get; set; } = _ => Task.FromResult(true);
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(string Method, object[] Arguments, CancellationToken Token)> Calls { get; } = [];
        public int ReadRowCalls { get; private set; }
        public int ReadAllCalls { get; private set; }
        public int ReadRowAsyncCalls { get; private set; }
        public int ReadAllAsyncCalls { get; private set; }

        [Obsolete("Use InsertRowAsync instead.")]
        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) =>
            Record(nameof(InsertRow), CancellationToken.None, entry, tableVersion);

        [Obsolete("Use UpdateRowAsync instead.")]
        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) =>
            Record(nameof(UpdateRow), CancellationToken.None, entry, etag, tableVersion);

        [Obsolete("Use ReadRowAsync instead.")]
        public Task<MembershipTableData> ReadRow(SiloAddress key)
        {
            ReadRowCalls++;
            throw new InvalidOperationException("A write result must not read a row.");
        }

        [Obsolete("Use ReadAllAsync instead.")]
        public Task<MembershipTableData> ReadAll()
        {
            ReadAllCalls++;
            throw new InvalidOperationException("A write result must not read the table.");
        }

        public Task<MembershipTableData> ReadRowAsync(SiloAddress key, CancellationToken cancellationToken = default)
        {
            ReadRowAsyncCalls++;
            throw new InvalidOperationException("A write result must not read a row asynchronously.");
        }

        public Task<MembershipTableData> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            ReadAllAsyncCalls++;
            throw new InvalidOperationException("A write result must not read the table asynchronously.");
        }

        [Obsolete("Use InitializeMembershipTableAsync instead.")]
        public Task InitializeMembershipTable(bool tryInitTableVersion) =>
            Unexpected(nameof(InitializeMembershipTable), tryInitTableVersion);

        [Obsolete("Use DeleteMembershipTableEntriesAsync instead.")]
        public Task DeleteMembershipTableEntries(string clusterId) =>
            Unexpected(nameof(DeleteMembershipTableEntries), clusterId);

        [Obsolete("Use CleanupDefunctSiloEntriesAsync instead.")]
        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) =>
            Unexpected(nameof(CleanupDefunctSiloEntries), beforeDate);

        [Obsolete("Use UpdateIAmAliveAsync instead.")]
        public Task UpdateIAmAlive(MembershipEntry entry) => Unexpected(nameof(UpdateIAmAlive), entry);

        protected Task<bool> Record(string method, CancellationToken token, params object[] arguments)
        {
            Calls.Add((method, arguments, token));
            Started.TrySetResult(true);
            return Write(token);
        }

        private Task Unexpected(string method, params object[] arguments)
        {
            Calls.Add((method, arguments, CancellationToken.None));
            throw new InvalidOperationException($"Unexpected membership operation: {method}");
        }
    }

    private sealed class NativeBoolProvider : LegacyBoolProvider, IMembershipTable
    {
        public Task<bool> InsertRowAsync(MembershipEntry entry, TableVersion tableVersion, CancellationToken cancellationToken = default) =>
            Record(nameof(InsertRowAsync), cancellationToken, entry, tableVersion);

        public Task<bool> UpdateRowAsync(MembershipEntry entry, string etag, TableVersion tableVersion, CancellationToken cancellationToken = default) =>
            Record(nameof(UpdateRowAsync), cancellationToken, entry, etag, tableVersion);
    }
}
