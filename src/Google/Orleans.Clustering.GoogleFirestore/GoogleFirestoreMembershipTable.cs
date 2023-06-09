using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Configuration;

namespace Orleans.Clustering.GoogleFirestore;

internal class GoogleFirestoreMembershipTable : IMembershipTable
{
    private readonly FirestoreOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly string _clusterId;
    private OrleansSiloInstanceManager _instanceManager = default!;

    public GoogleFirestoreMembershipTable(
        ILoggerFactory loggerFactory,
        IOptions<FirestoreOptions> options,
        IOptions<ClusterOptions> clusterOptions)
    {
        this._loggerFactory = loggerFactory;
        this._logger = loggerFactory.CreateLogger<GoogleFirestoreMembershipTable>();
        this._options = options.Value;
        this._clusterId = clusterOptions.Value.ClusterId;
    }

    public async Task InitializeMembershipTable(bool tryInitTableVersion)
    {
        this._instanceManager = await OrleansSiloInstanceManager.GetManager(
            this._clusterId,
            this._loggerFactory,
            this._options);

        if (tryInitTableVersion)
        {
            var created = await this._instanceManager.TryCreateTableVersionEntryAsync();
            if (created) this._logger.LogInformation("Created new table version row.");
        }
    }

    public Task DeleteMembershipTableEntries(string clusterId) => this._instanceManager.DeleteTableEntries();

    public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => this._instanceManager.CleanupDefunctSiloEntries(beforeDate);

    public async Task<MembershipTableData> ReadRow(SiloAddress key)
    {
        try
        {
            var data = await this._instanceManager.FindSiloAndVersionEntities(key);

            var table = Convert((new[] { data.Silo }, data.Version));

            if (this._logger.IsEnabled(LogLevel.Debug)) this._logger.LogDebug("Read my entry {SiloAddress} Table={Data}", key.ToString(), data.ToString());

            return table;
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(exc,
                "Intermediate error reading silo entry for key {SiloAddress} from the Firestore.", key.ToString());
            throw;
        }
    }

    public async Task<MembershipTableData> ReadAll()
    {
        try
        {
            var entries = await this._instanceManager.FindAllSiloEntries();
            var data = Convert(entries);
            if (this._logger.IsEnabled(LogLevel.Trace)) this._logger.LogTrace("ReadAll Table={Data}", data.ToString());

            return data;
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(
                exc,
                "Intermediate error reading all silo entries from Firestore.");
            throw;
        }
    }

    public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
    {
        try
        {
            if (this._logger.IsEnabled(LogLevel.Debug)) this._logger.LogDebug("InsertRow entry = {Data}, table version = {TableVersion}", entry.ToString(), tableVersion);

            var silo = SiloInstanceEntity.FromMembershipEntry(entry, this._clusterId);
            var version = this._instanceManager.CreateClusterVersionEntity(tableVersion.Version);
            version.ETag = Utils.ParseTimestamp(tableVersion.VersionEtag);

            var result = await this._instanceManager.InsertSiloEntryConditionally(silo, version);

            if (result == false)
                this._logger.LogWarning(
                    "Insert failed due to contention on the table. Will retry. Entry {Data}, table version = {TableVersion}", entry.ToString(), tableVersion);
            return result;
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(
                exc,
                "Intermediate error inserting entry {Data} tableVersion {TableVersion} to Firestore}.", entry.ToString(), tableVersion == null ? "null" : tableVersion.ToString());
            throw;
        }
    }

    public async Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
    {
        try
        {
            if (this._logger.IsEnabled(LogLevel.Debug)) this._logger.LogDebug("UpdateRow entry = {Data}, etag = {ETag}, table version = {TableVersion}", entry.ToString(), etag, tableVersion);

            var silo = SiloInstanceEntity.FromMembershipEntry(entry, this._clusterId);
            silo.ETag = Utils.ParseTimestamp(etag);
            var version = this._instanceManager.CreateClusterVersionEntity(tableVersion.Version);
            version.ETag = Utils.ParseTimestamp(tableVersion.VersionEtag);

            var result = await this._instanceManager.UpdateSiloEntryConditionally(silo, version);
            if (result == false)
                this._logger.LogWarning(
                    "Update failed due to contention on the table. Will retry. Entry {Data}, eTag {ETag}, table version = {TableVersion}",
                    entry.ToString(),
                    etag,
                    tableVersion);
            return result;
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(
                exc,
                "Intermediate error updating entry {Data} tableVersion {TableVersion} to Firestore.", entry.ToString(), tableVersion == null ? "null" : tableVersion.ToString());
            throw;
        }
    }

    public async Task UpdateIAmAlive(MembershipEntry entry)
    {
        try
        {
            if (this._logger.IsEnabled(LogLevel.Debug)) this._logger.LogDebug("Merge entry = {Data}", entry.ToString());

            var silo = SiloInstanceEntity.FromMembershipEntry(entry, this._clusterId);

            await this._instanceManager.MergeTableEntryAsync(silo.GetIAmAliveFields(), silo.Id);
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(
                exc,
                "Intermediate error updating IAmAlive field for entry {Data} to Firestore.", entry.ToString());
            throw;
        }
    }


    private static MembershipTableData Convert((SiloInstanceEntity[] Silos, ClusterVersionEntity Version) data)
    {
        return new MembershipTableData
        (
            data.Silos.Select(s => Tuple.Create(s.ToMembershipEntry(), Utils.FormatTimestamp(s.ETag))).ToList(),
            data.Version.ToTableVersion()
        );
    }
}