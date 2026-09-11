using System.Reflection;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
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
        nameof(IMembershipTable.InitializeMembershipTableAsync),
        nameof(IMembershipTable.DeleteMembershipTableEntriesAsync),
        nameof(IMembershipTable.CleanupDefunctSiloEntriesAsync),
        nameof(IMembershipTable.ReadRowAsync),
        nameof(IMembershipTable.ReadAllAsync),
        nameof(IMembershipTable.InsertRowAsync),
        nameof(IMembershipTable.UpdateRowAsync),
        nameof(IMembershipTable.UpdateIAmAliveAsync),
    };

    [Theory]
    [InlineData(nameof(IMembershipTable.InitializeMembershipTableAsync), "FB89E5E9")]
    [InlineData(nameof(IMembershipTable.DeleteMembershipTableEntriesAsync), "BF899C85")]
    [InlineData(nameof(IMembershipTable.CleanupDefunctSiloEntriesAsync), "7A519C2E")]
    [InlineData(nameof(IMembershipTable.ReadRowAsync), "D851FB33")]
    [InlineData(nameof(IMembershipTable.ReadAllAsync), "00BCE16F")]
    [InlineData(nameof(IMembershipTable.InsertRowAsync), "FEF3AC5A")]
    [InlineData(nameof(IMembershipTable.UpdateRowAsync), "E06D3DBC")]
    [InlineData(nameof(IMembershipTable.UpdateIAmAliveAsync), "B1A52D2B")]
    public void CancellationOverload_UsesLegacyWireIdentity(string methodName, string legacyId)
    {
        var method = Assert.Single(typeof(IMembershipTable).GetMethods(),
            candidate => candidate.Name == methodName && candidate.GetParameters().LastOrDefault()?.ParameterType == typeof(CancellationToken));
        var legacyMethod = typeof(IMembershipTable).GetMethod(
            methodName[..^"Async".Length],
            method.GetParameters().SkipLast(1).Select(parameter => parameter.ParameterType).ToArray());
        Assert.NotNull(legacyMethod);
        Assert.Contains(methodName, Assert.IsType<ObsoleteAttribute>(legacyMethod.GetCustomAttribute<ObsoleteAttribute>()).Message, StringComparison.Ordinal);
        Assert.Equal(legacyId, Assert.Single(method.GetCustomAttributes<AliasAttribute>()).Alias);
        var invokableType = Assert.Single(typeof(IMembershipTable).Assembly.GetTypes(),
            type => typeof(IInvokable).IsAssignableFrom(type)
                && type.GetCustomAttributes<CompoundTypeAliasAttribute>().Any(
                    alias => alias.Components.SequenceEqual(new object[] { "inv", typeof(GrainReference), typeof(IMembershipTable), legacyId })));
        using var invokable = Assert.IsAssignableFrom<IInvokable>(Activator.CreateInstance(invokableType));
        Assert.Equal(method, invokable.GetMethod());
        Assert.True(invokable.IsCancellable);
        Assert.Equal(typeof(IMembershipTable), invokable.GetInterfaceType());
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task DefaultCancellationOverload_PreservesLegacyArgumentsAndResult(string operation)
    {
        var provider = new LegacyProvider();
        provider.Completion.SetResult(true);

        var result = await Invoke(provider, operation, TestContext.Current.CancellationToken);

        var call = Assert.Single(provider.Calls);
        Assert.Equal(operation[..^"Async".Length], call.Method);
        Assert.Equal(ExpectedArguments(provider, operation), call.Arguments);
        object? expected = operation switch
        {
            nameof(IMembershipTable.ReadAllAsync) or nameof(IMembershipTable.ReadRowAsync) => provider.Data,
            nameof(IMembershipTable.InsertRowAsync) or nameof(IMembershipTable.UpdateRowAsync) => true,
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

        var result = await table.ReadAllAsync(cancellation.Token);

        Assert.Equal(cancellation.Token, provider.ReceivedToken);
        Assert.Same(provider.Data, result);
        Assert.Empty(provider.Calls);
    }

    private static async Task<object?> Invoke(LegacyProvider provider, string operation, CancellationToken cancellationToken)
    {
        IMembershipTable table = provider;
        switch (operation)
        {
            case nameof(IMembershipTable.InitializeMembershipTableAsync):
                await table.InitializeMembershipTableAsync(true, cancellationToken);
                break;
            case nameof(IMembershipTable.DeleteMembershipTableEntriesAsync):
                await table.DeleteMembershipTableEntriesAsync("cluster", cancellationToken);
                break;
            case nameof(IMembershipTable.CleanupDefunctSiloEntriesAsync):
                await table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.UnixEpoch, cancellationToken);
                break;
            case nameof(IMembershipTable.ReadRowAsync):
                return await table.ReadRowAsync(provider.Entry.SiloAddress, cancellationToken);
            case nameof(IMembershipTable.ReadAllAsync):
                return await table.ReadAllAsync(cancellationToken);
            case nameof(IMembershipTable.InsertRowAsync):
                return await table.InsertRowAsync(provider.Entry, provider.Data.Version, cancellationToken);
            case nameof(IMembershipTable.UpdateRowAsync):
                return await table.UpdateRowAsync(provider.Entry, "etag", provider.Data.Version, cancellationToken);
            case nameof(IMembershipTable.UpdateIAmAliveAsync):
                await table.UpdateIAmAliveAsync(provider.Entry, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }

        return null;
    }

    private static object[] ExpectedArguments(LegacyProvider provider, string operation) => operation switch
    {
        nameof(IMembershipTable.InitializeMembershipTableAsync) => [true],
        nameof(IMembershipTable.DeleteMembershipTableEntriesAsync) => ["cluster"],
        nameof(IMembershipTable.CleanupDefunctSiloEntriesAsync) => [DateTimeOffset.UnixEpoch],
        nameof(IMembershipTable.ReadRowAsync) => [provider.Entry.SiloAddress],
        nameof(IMembershipTable.ReadAllAsync) => [],
        nameof(IMembershipTable.InsertRowAsync) => [provider.Entry, provider.Data.Version],
        nameof(IMembershipTable.UpdateRowAsync) => [provider.Entry, "etag", provider.Data.Version],
        nameof(IMembershipTable.UpdateIAmAliveAsync) => [provider.Entry],
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private class LegacyProvider : IMembershipTable
    {
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(string Method, object[] Arguments)> Calls { get; } = [];
        public MembershipEntry Entry { get; } = new() { SiloAddress = SiloAddress.FromParsableString("127.0.0.1:100@100") };
        public MembershipTableData Data { get; } = new(new TableVersion(1, "version"));

        [Obsolete("Use InitializeMembershipTableAsync instead.")]
        public Task InitializeMembershipTable(bool tryInitTableVersion) => Record(nameof(InitializeMembershipTable), tryInitTableVersion);
        [Obsolete("Use DeleteMembershipTableEntriesAsync instead.")]
        public Task DeleteMembershipTableEntries(string clusterId) => Record(nameof(DeleteMembershipTableEntries), clusterId);
        [Obsolete("Use CleanupDefunctSiloEntriesAsync instead.")]
        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => Record(nameof(CleanupDefunctSiloEntries), beforeDate);
        [Obsolete("Use InsertRowAsync instead.")]
        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => Record(nameof(InsertRow), entry, tableVersion);
        [Obsolete("Use UpdateRowAsync instead.")]
        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => Record(nameof(UpdateRow), entry, etag, tableVersion);
        [Obsolete("Use UpdateIAmAliveAsync instead.")]
        public Task UpdateIAmAlive(MembershipEntry entry) => Record(nameof(UpdateIAmAlive), entry);

        [Obsolete("Use ReadRowAsync instead.")]
        public async Task<MembershipTableData> ReadRow(SiloAddress key)
        {
            await Record(nameof(ReadRow), key);
            return Data;
        }

        [Obsolete("Use ReadAllAsync instead.")]
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

        public Task<MembershipTableData> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReceivedToken = cancellationToken;
            return Task.FromResult(Data);
        }
    }
}
