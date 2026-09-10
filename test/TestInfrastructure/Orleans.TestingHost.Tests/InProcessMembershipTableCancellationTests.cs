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
    [InlineData(nameof(IMembershipTable.InitializeMembershipTable))]
    [InlineData(nameof(IMembershipTable.DeleteMembershipTableEntries))]
    [InlineData(nameof(IMembershipTable.CleanupDefunctSiloEntries))]
    [InlineData(nameof(IMembershipTable.ReadRow))]
    [InlineData(nameof(IMembershipTable.ReadAll))]
    [InlineData(nameof(IMembershipTable.InsertRow))]
    [InlineData(nameof(IMembershipTable.UpdateRow))]
    [InlineData(nameof(IMembershipTable.UpdateIAmAlive))]
    public async Task PreCanceledOperation_PreservesTable(string operation)
    {
        var testToken = TestContext.Current.CancellationToken;
        IMembershipTable table = new InProcessMembershipTable("cluster");
        await table.InitializeMembershipTable(true, testToken);
        var initial = await table.ReadAll(testToken);
        var entry = new MembershipEntry
        {
            SiloAddress = SiloAddress.FromParsableString("127.0.0.1:100@100"),
            Status = SiloStatus.Dead,
            StartTime = DateTime.UnixEpoch,
            IAmAliveTime = DateTime.UnixEpoch,
        };
        Assert.True(await table.InsertRow(entry, initial.Version.Next(), testToken));
        var before = await table.ReadAll(testToken);
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
        var after = await table.ReadAll(testToken);
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
            nameof(IMembershipTable.InitializeMembershipTable) => table.InitializeMembershipTable(true, cancellationToken),
            nameof(IMembershipTable.DeleteMembershipTableEntries) => table.DeleteMembershipTableEntries("cluster", cancellationToken),
            nameof(IMembershipTable.CleanupDefunctSiloEntries) => table.CleanupDefunctSiloEntries(DateTimeOffset.UnixEpoch.AddDays(1), cancellationToken),
            nameof(IMembershipTable.ReadRow) => table.ReadRow(entry.SiloAddress, cancellationToken),
            nameof(IMembershipTable.ReadAll) => table.ReadAll(cancellationToken),
            nameof(IMembershipTable.InsertRow) => table.InsertRow(entry, version, cancellationToken),
            nameof(IMembershipTable.UpdateRow) => table.UpdateRow(entry, etag, version, cancellationToken),
            nameof(IMembershipTable.UpdateIAmAlive) => table.UpdateIAmAlive(entry, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
}
