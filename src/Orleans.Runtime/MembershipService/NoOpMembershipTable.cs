#nullable enable
using System;
using System.Threading.Tasks;
using Orleans.Configuration;

namespace Orleans.Runtime.MembershipService;

/// <summary>
/// A no-op implementation of <see cref="IMembershipTable"/> that serves as a fallback
/// when a custom <see cref="IMembershipManager"/> is registered.
/// </summary>
/// <remarks>
/// <para>
/// This implementation is used when an external membership provider (like RapidCluster)
/// registers its own <see cref="IMembershipManager"/> implementation directly, bypassing
/// the traditional <see cref="IMembershipTable"/>-based approach.
/// </para>
/// <para>
/// When a custom <see cref="IMembershipManager"/> is registered:
/// </para>
/// <list type="bullet">
/// <item><description>The custom implementation handles all membership operations</description></item>
/// <item><description><see cref="MembershipTableManager"/> is still instantiated but its <see cref="IMembershipManager"/>
/// interface is not used (the custom implementation takes precedence via TryAddFromExisting)</description></item>
/// <item><description>This <see cref="NoOpMembershipTable"/> satisfies <see cref="MembershipTableManager"/>'s
/// constructor dependency without providing actual functionality</description></item>
/// </list>
/// <para>
/// All read operations return empty/minimal valid data. All write operations succeed without doing anything.
/// If the <see cref="MembershipTableManager"/> lifecycle methods are called (because it's registered as
/// a lifecycle participant), they will complete without error but won't provide meaningful membership data.
/// </para>
/// </remarks>
internal sealed class NoOpMembershipTable : IMembershipTable
{
    private static readonly TableVersion InitialTableVersion = new(0, "0");

    /// <inheritdoc />
    public Task InitializeMembershipTable(bool tryInitTableVersion)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteMembershipTableEntries(string clusterId)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<MembershipTableData> ReadRow(SiloAddress key)
    {
        // Return empty table data - no entries found
        return Task.FromResult(new MembershipTableData(InitialTableVersion));
    }

    /// <inheritdoc />
    public Task<MembershipTableData> ReadAll()
    {
        // Return empty table data - no entries
        return Task.FromResult(new MembershipTableData(InitialTableVersion));
    }

    /// <inheritdoc />
    public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
    {
        // Pretend insert succeeded
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
    {
        // Pretend update succeeded
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task UpdateIAmAlive(MembershipEntry entry)
    {
        return Task.CompletedTask;
    }
}
