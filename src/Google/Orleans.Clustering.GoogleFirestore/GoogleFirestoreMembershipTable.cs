using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Configuration;

namespace Orleans.Clustering.GoogleFirestore;

internal partial class GoogleFirestoreMembershipTable : IMembershipTable
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
            Utils.SanitizeId(this._clusterId),
            this._loggerFactory,
            this._options);

        if (tryInitTableVersion)
        {
            var created = await this._instanceManager.TryCreateTableVersionEntryAsync();
            if (created) LogCreatedTableVersion();
        }
    }

    public async Task DeleteMembershipTableEntries(string clusterId)
    {
        var manager = clusterId == this._clusterId
            ? this._instanceManager
            : await OrleansSiloInstanceManager.GetManager(Utils.SanitizeId(clusterId), this._loggerFactory, this._options);
        await manager.DeleteTableEntries();
    }

    public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => this._instanceManager.CleanupDefunctSiloEntries(beforeDate);

    public async Task<MembershipTableData> ReadRow(SiloAddress key)
    {
        try
        {
            var data = await this._instanceManager.FindSiloAndVersionEntities(key);

            var table = Convert((new[] { data.Silo }, data.Version));

            LogReadEntry(key, table);

            return table;
        }
        catch (Exception exc)
        {
            LogReadEntryError(exc, key);
            throw;
        }
    }

    public async Task<MembershipTableData> ReadAll()
    {
        try
        {
            var entries = await this._instanceManager.FindAllSiloEntries();
            var data = Convert(entries);
            LogReadAll(data);

            return data;
        }
        catch (Exception exc)
        {
            LogReadAllError(exc);
            throw;
        }
    }

    public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
    {
        try
        {
            LogInsertRow(entry, tableVersion);

            var silo = SiloInstanceEntity.FromMembershipEntry(entry, this._clusterId);
            var version = this._instanceManager.CreateClusterVersionEntity(tableVersion.Version);
            version.ETag = Utils.ParseTimestamp(tableVersion.VersionEtag);

            var result = await this._instanceManager.InsertSiloEntryConditionally(silo, version);

            if (result == false)
                LogInsertContention(entry, tableVersion);
            return result;
        }
        catch (Exception exc)
        {
            LogInsertError(exc, entry, tableVersion);
            throw;
        }
    }

    public async Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
    {
        try
        {
            LogUpdateRow(entry, etag, tableVersion);

            var silo = SiloInstanceEntity.FromMembershipEntry(entry, this._clusterId);
            silo.ETag = Utils.ParseTimestamp(etag);
            var version = this._instanceManager.CreateClusterVersionEntity(tableVersion.Version);
            version.ETag = Utils.ParseTimestamp(tableVersion.VersionEtag);

            var result = await this._instanceManager.UpdateSiloEntryConditionally(silo, version);
            if (result == false)
                LogUpdateContention(entry, etag, tableVersion);
            return result;
        }
        catch (Exception exc)
        {
            LogUpdateError(exc, entry, tableVersion);
            throw;
        }
    }

    public async Task UpdateIAmAlive(MembershipEntry entry)
    {
        try
        {
            LogMergeEntry(entry);

            var silo = SiloInstanceEntity.FromMembershipEntry(entry, this._clusterId);

            await this._instanceManager.MergeTableEntryAsync(silo.GetIAmAliveFields(), silo.Id);
        }
        catch (Exception exc)
        {
            LogUpdateIAmAliveError(exc, entry);
            throw;
        }
    }


    private static MembershipTableData Convert((SiloInstanceEntity[] Silos, ClusterVersionEntity Version) data)
    {
        return new MembershipTableData
        (
            data.Silos.Select(s => Tuple.Create(s.ToMembershipEntry(), Utils.FormatTimestamp(s.ETag!.Value))).ToList(),
            data.Version.ToTableVersion()
        );
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Created new table version row.")]
    private partial void LogCreatedTableVersion();

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Read my entry {SiloAddress} Table={Data}")]
    private partial void LogReadEntry(SiloAddress siloAddress, MembershipTableData data);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Intermediate error reading silo entry for key {SiloAddress} from Firestore.")]
    private partial void LogReadEntryError(Exception exception, SiloAddress siloAddress);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "ReadAll Table={Data}")]
    private partial void LogReadAll(MembershipTableData data);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Intermediate error reading all silo entries from Firestore.")]
    private partial void LogReadAllError(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "InsertRow entry = {Data}, table version = {TableVersion}")]
    private partial void LogInsertRow(MembershipEntry data, TableVersion tableVersion);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Insert failed due to contention on the table. Will retry. Entry {Data}, table version = {TableVersion}")]
    private partial void LogInsertContention(MembershipEntry data, TableVersion tableVersion);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Intermediate error inserting entry {Data} tableVersion {TableVersion} to Firestore.")]
    private partial void LogInsertError(Exception exception, MembershipEntry data, TableVersion tableVersion);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "UpdateRow entry = {Data}, etag = {ETag}, table version = {TableVersion}")]
    private partial void LogUpdateRow(MembershipEntry data, string etag, TableVersion tableVersion);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Update failed due to contention on the table. Will retry. Entry {Data}, eTag {ETag}, table version = {TableVersion}")]
    private partial void LogUpdateContention(MembershipEntry data, string etag, TableVersion tableVersion);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Intermediate error updating entry {Data} tableVersion {TableVersion} to Firestore.")]
    private partial void LogUpdateError(Exception exception, MembershipEntry data, TableVersion tableVersion);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Merge entry = {Data}")]
    private partial void LogMergeEntry(MembershipEntry data);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Intermediate error updating IAmAlive field for entry {Data} to Firestore.")]
    private partial void LogUpdateIAmAliveError(Exception exception, MembershipEntry data);
}