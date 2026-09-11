using NSubstitute;
using Orleans;
using Orleans.Runtime;

namespace NonSilo.Tests.Membership;

// A concrete implementation ensures interface defaults run when the inner provider is a substitute.
#pragma warning disable CS0618 // This fixture configures and exercises legacy-provider compatibility.
internal sealed class LegacyMembershipTable(IMembershipTable inner) : IMembershipTable
{
    public IMembershipTable Inner => inner;

    public void ConfigureReadAll(MembershipTableData result) => ConfigureReadAll(Task.FromResult(result));
    public void ConfigureReadAll(Task<MembershipTableData> result) => inner.ReadAll().Returns(result);
    public void ConfigureReadAll(Func<Task<MembershipTableData>> read) => inner.ReadAll().Returns(_ => read());
    public void ConfigureInsertRow(Task<bool> result) => inner.InsertRow(Arg.Any<MembershipEntry>(), Arg.Any<TableVersion>()).Returns(result);
    public void ConfigureUpdateIAmAlive(Task result) => inner.UpdateIAmAlive(Arg.Any<MembershipEntry>()).Returns(result);

    [Obsolete("Use InitializeMembershipTableAsync instead.")]
    public Task InitializeMembershipTable(bool tryInitTableVersion) => inner.InitializeMembershipTable(tryInitTableVersion);
    [Obsolete("Use DeleteMembershipTableEntriesAsync instead.")]
    public Task DeleteMembershipTableEntries(string clusterId) => inner.DeleteMembershipTableEntries(clusterId);
    [Obsolete("Use CleanupDefunctSiloEntriesAsync instead.")]
    public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => inner.CleanupDefunctSiloEntries(beforeDate);
    [Obsolete("Use ReadRowAsync instead.")]
    public Task<MembershipTableData> ReadRow(SiloAddress key) => inner.ReadRow(key);
    [Obsolete("Use ReadAllAsync instead.")]
    public Task<MembershipTableData> ReadAll() => inner.ReadAll();
    [Obsolete("Use InsertRowAsync instead.")]
    public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => inner.InsertRow(entry, tableVersion);
    [Obsolete("Use UpdateRowAsync instead.")]
    public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => inner.UpdateRow(entry, etag, tableVersion);
    [Obsolete("Use UpdateIAmAliveAsync instead.")]
    public Task UpdateIAmAlive(MembershipEntry entry) => inner.UpdateIAmAlive(entry);
}
#pragma warning restore CS0618
