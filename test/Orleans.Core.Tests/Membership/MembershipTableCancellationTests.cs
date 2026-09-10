using Orleans;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace NonSilo.Tests.Membership;

[TestCategory("BVT"), TestCategory("Membership")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class MembershipTableCancellationTests
{
    public static TheoryData<string> Operations { get; } = new()
    {
        nameof(IMembershipTable.InitializeMembershipTable),
        nameof(IMembershipTable.DeleteMembershipTableEntries),
        nameof(IMembershipTable.CleanupDefunctSiloEntries),
        nameof(IMembershipTable.ReadRow),
        nameof(IMembershipTable.ReadAll),
        nameof(IMembershipTable.InsertRow),
        nameof(IMembershipTable.UpdateRow),
        nameof(IMembershipTable.UpdateIAmAlive),
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task DefaultCancellationOverload_PreservesLegacyArgumentsAndResult(string operation)
    {
        var provider = new LegacyProvider();
        provider.Completion.SetResult(true);

        var result = await Invoke(provider, operation, TestContext.Current.CancellationToken);

        var call = Assert.Single(provider.Calls);
        Assert.Equal(operation, call.Method);
        Assert.Equal(ExpectedArguments(provider, operation), call.Arguments);
        object? expected = operation switch
        {
            nameof(IMembershipTable.ReadAll) or nameof(IMembershipTable.ReadRow) => provider.Data,
            nameof(IMembershipTable.InsertRow) or nameof(IMembershipTable.UpdateRow) => true,
            _ => null,
        };
        Assert.Equal(expected, result);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task DefaultCancellationOverload_PreCanceled_SkipsLegacyOperation(string operation)
    {
        var provider = new LegacyProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Invoke(provider, operation, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(provider.Calls);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task DefaultCancellationOverload_CancelsWaitAndRetainsProviderLifetime(string operation)
    {
        var provider = new LegacyProvider();
        using var cancellation = new CancellationTokenSource();
        var pending = Invoke(provider, operation, cancellation.Token);
        Assert.Single(provider.Calls);

        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(pending.IsCanceled);
        Assert.False(provider.Completion.Task.IsCompleted);
        provider.Completion.SetException(new InvalidOperationException("Late tokenless provider failure"));
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task DefaultCancellationOverload_PropagatesProviderFailure(string operation)
    {
        var provider = new LegacyProvider();
        var failure = new InvalidOperationException("Provider failure");
        provider.Completion.SetException(failure);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Invoke(provider, operation, TestContext.Current.CancellationToken));

        Assert.Same(failure, exception);
        Assert.Single(provider.Calls);
    }

    [Fact]
    public async Task NativeCancellationOverride_ReceivesTokenAndOwnsResult()
    {
        var provider = new NativeProvider();
        IMembershipTable table = provider;
        using var cancellation = new CancellationTokenSource();

        var result = await table.ReadAll(cancellation.Token);

        Assert.Equal(cancellation.Token, provider.ReceivedToken);
        Assert.Same(provider.Data, result);
        Assert.Empty(provider.Calls);
    }

    private static async Task<object?> Invoke(LegacyProvider provider, string operation, CancellationToken cancellationToken)
    {
        IMembershipTable table = provider;
        switch (operation)
        {
            case nameof(IMembershipTable.InitializeMembershipTable):
                await table.InitializeMembershipTable(true, cancellationToken);
                break;
            case nameof(IMembershipTable.DeleteMembershipTableEntries):
                await table.DeleteMembershipTableEntries("cluster", cancellationToken);
                break;
            case nameof(IMembershipTable.CleanupDefunctSiloEntries):
                await table.CleanupDefunctSiloEntries(DateTimeOffset.UnixEpoch, cancellationToken);
                break;
            case nameof(IMembershipTable.ReadRow):
                return await table.ReadRow(provider.Entry.SiloAddress, cancellationToken);
            case nameof(IMembershipTable.ReadAll):
                return await table.ReadAll(cancellationToken);
            case nameof(IMembershipTable.InsertRow):
                return await table.InsertRow(provider.Entry, provider.Data.Version, cancellationToken);
            case nameof(IMembershipTable.UpdateRow):
                return await table.UpdateRow(provider.Entry, "etag", provider.Data.Version, cancellationToken);
            case nameof(IMembershipTable.UpdateIAmAlive):
                await table.UpdateIAmAlive(provider.Entry, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }

        return null;
    }

    private static object[] ExpectedArguments(LegacyProvider provider, string operation) => operation switch
    {
        nameof(IMembershipTable.InitializeMembershipTable) => [true],
        nameof(IMembershipTable.DeleteMembershipTableEntries) => ["cluster"],
        nameof(IMembershipTable.CleanupDefunctSiloEntries) => [DateTimeOffset.UnixEpoch],
        nameof(IMembershipTable.ReadRow) => [provider.Entry.SiloAddress],
        nameof(IMembershipTable.ReadAll) => [],
        nameof(IMembershipTable.InsertRow) => [provider.Entry, provider.Data.Version],
        nameof(IMembershipTable.UpdateRow) => [provider.Entry, "etag", provider.Data.Version],
        nameof(IMembershipTable.UpdateIAmAlive) => [provider.Entry],
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private class LegacyProvider : IMembershipTable
    {
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(string Method, object[] Arguments)> Calls { get; } = [];
        public MembershipEntry Entry { get; } = new() { SiloAddress = SiloAddress.FromParsableString("127.0.0.1:100@100") };
        public MembershipTableData Data { get; } = new(new TableVersion(1, "version"));

        public Task InitializeMembershipTable(bool tryInitTableVersion) => Record(nameof(InitializeMembershipTable), tryInitTableVersion);
        public Task DeleteMembershipTableEntries(string clusterId) => Record(nameof(DeleteMembershipTableEntries), clusterId);
        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => Record(nameof(CleanupDefunctSiloEntries), beforeDate);
        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => Record(nameof(InsertRow), entry, tableVersion);
        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => Record(nameof(UpdateRow), entry, etag, tableVersion);
        public Task UpdateIAmAlive(MembershipEntry entry) => Record(nameof(UpdateIAmAlive), entry);

        public async Task<MembershipTableData> ReadRow(SiloAddress key)
        {
            await Record(nameof(ReadRow), key);
            return Data;
        }

        public async Task<MembershipTableData> ReadAll()
        {
            await Record(nameof(ReadAll));
            return Data;
        }

        private Task<bool> Record(string method, params object[] arguments)
        {
            Calls.Add((method, arguments));
            return Completion.Task;
        }
    }

    private sealed class NativeProvider : LegacyProvider, IMembershipTable
    {
        public CancellationToken ReceivedToken { get; private set; }

        public Task<MembershipTableData> ReadAll(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReceivedToken = cancellationToken;
            return Task.FromResult(Data);
        }
    }
}
