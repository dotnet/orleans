using Orleans.Runtime;
using Orleans.TestingHost.InProcess;
using TestExtensions;
using Xunit;

namespace Orleans.TestingHost.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("TestingHost")]
[TestCategory("BVT")]
public sealed class InProcessMembershipTableCancellationTests
{
    [Theory]
    [InlineData(nameof(IMembershipTable.InitializeMembershipTableAsync))]
    [InlineData(nameof(IMembershipTable.DeleteMembershipTableEntriesAsync))]
    [InlineData(nameof(IMembershipTable.CleanupDefunctSiloEntriesAsync))]
    [InlineData(nameof(IMembershipTable.ReadRowAsync))]
    [InlineData(nameof(IMembershipTable.ReadAllAsync))]
    [InlineData(nameof(IMembershipTable.InsertRowAsync))]
    [InlineData(nameof(IMembershipTable.UpdateRowAsync))]
    [InlineData(nameof(IMembershipTable.UpdateIAmAliveAsync))]
    public async Task PreCanceledOperation_PreservesTable(string operation)
    {
        var testToken = TestContext.Current.CancellationToken;
        IMembershipTable table = new InProcessMembershipTable("cluster");
        await table.InitializeMembershipTableAsync(true, testToken);
        var initial = await table.ReadAllAsync(testToken);
        var entry = new MembershipEntry
        {
            SiloAddress = SiloAddress.FromParsableString("127.0.0.1:100@100"),
            Status = SiloStatus.Dead,
            StartTime = DateTime.UnixEpoch,
            IAmAliveTime = DateTime.UnixEpoch,
        };
        Assert.True(await table.InsertRowAsync(entry, initial.Version.Next(), testToken));
        var before = await table.ReadAllAsync(testToken);
        var row = Assert.Single(before.Members);
        var changedEntry = new MembershipEntry
        {
            SiloAddress = entry.SiloAddress,
            Status = entry.Status,
            StartTime = entry.StartTime,
            IAmAliveTime = DateTime.UnixEpoch.AddMinutes(1),
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Invoke(table, operation, changedEntry, row.Item2, before.Version.Next(), cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        var after = await table.ReadAllAsync(testToken);
        Assert.Equal(before.Version, after.Version);
        var unchanged = Assert.Single(after.Members);
        Assert.Equal(row.Item2, unchanged.Item2);
        Assert.Equal(entry.IAmAliveTime, unchanged.Item1.IAmAliveTime);
        Assert.Equal(entry.Status, unchanged.Item1.Status);
    }

    private static Task Invoke(
        IMembershipTable table,
        string operation,
        MembershipEntry entry,
        string etag,
        TableVersion version,
        CancellationToken cancellationToken) => operation switch
        {
            nameof(IMembershipTable.InitializeMembershipTableAsync) => table.InitializeMembershipTableAsync(true, cancellationToken),
            nameof(IMembershipTable.DeleteMembershipTableEntriesAsync) => table.DeleteMembershipTableEntriesAsync("cluster", cancellationToken),
            nameof(IMembershipTable.CleanupDefunctSiloEntriesAsync) => table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.UnixEpoch.AddDays(1), cancellationToken),
            nameof(IMembershipTable.ReadRowAsync) => table.ReadRowAsync(entry.SiloAddress, cancellationToken),
            nameof(IMembershipTable.ReadAllAsync) => table.ReadAllAsync(cancellationToken),
            nameof(IMembershipTable.InsertRowAsync) => table.InsertRowAsync(entry, version, cancellationToken),
            nameof(IMembershipTable.UpdateRowAsync) => table.UpdateRowAsync(entry, etag, version, cancellationToken),
            nameof(IMembershipTable.UpdateIAmAliveAsync) => table.UpdateIAmAliveAsync(entry, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
}
