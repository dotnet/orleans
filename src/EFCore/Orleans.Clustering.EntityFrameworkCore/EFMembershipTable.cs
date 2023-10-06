using System;
using System.Net;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Configuration;
using Orleans.Clustering.EntityFrameworkCore.Data;

namespace Orleans.Clustering.EntityFrameworkCore;

internal class EFMembershipTable<TDbContext, TETag> : IMembershipTable where TDbContext : ClusterDbContext<TDbContext, TETag>
{
    private readonly ILogger _logger;
    private readonly string _clusterId;
    private readonly IDbContextFactory<TDbContext> _dbContextFactory;
    private readonly IEFClusterETagConverter<TETag> _etagConverter;
    private SiloRecord<TETag>? _self;

    public EFMembershipTable(
        ILoggerFactory loggerFactory,
        IOptions<ClusterOptions> clusterOptions,
        IDbContextFactory<TDbContext> dbContextFactory,
        IEFClusterETagConverter<TETag> etagConverter)
    {
        this._logger = loggerFactory.CreateLogger<EFMembershipTable<TDbContext, TETag>>();
        this._clusterId = clusterOptions.Value.ClusterId;
        this._dbContextFactory = dbContextFactory;
        this._etagConverter = etagConverter;
    }

    public async Task InitializeMembershipTable(bool tryInitTableVersion)
    {
        if (!tryInitTableVersion) return;

        try
        {
            var ctx = this._dbContextFactory.CreateDbContext();

            var record = await ctx.Clusters
                .AsNoTracking()
                .SingleOrDefaultAsync(c => c.Id == this._clusterId)
                .ConfigureAwait(false);

            if (record is not null) return;

            record = new ClusterRecord<TETag> {Version = 0, Id = this._clusterId, Timestamp = DateTimeOffset.UtcNow};

            ctx.Clusters.Add(record);
            await ctx.SaveChangesAsync().ConfigureAwait(false);

            if (this._logger.IsEnabled(LogLevel.Debug))
            {
                this._logger.LogDebug("Created new Cluster record");
            }
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Unable to initialize Cluster membership table");
            WrappedException.CreateAndRethrow(ex);
        }
    }

    public async Task DeleteMembershipTableEntries(string clusterId)
    {
        try
        {
            var ctx = this._dbContextFactory.CreateDbContext();

            var silos = await ctx.Silos.Where(s => s.ClusterId == this._clusterId).ToArrayAsync().ConfigureAwait(false);
            if (silos.Length > 0)
            {
                ctx.Silos.RemoveRange(silos);
            }

            var cluster = await ctx.Clusters.SingleOrDefaultAsync(s => s.Id == this._clusterId);
            if (cluster is not null)
            {
                ctx.Clusters.Remove(cluster);
            }

            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error deleting membership table entries");
            WrappedException.CreateAndRethrow(ex);
        }
    }

    public async Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
    {
        try
        {
            var ctx = this._dbContextFactory.CreateDbContext();

            var silos = await ctx.Silos
                .Where(s =>
                    s.ClusterId == this._clusterId &&
                    s.Status != SiloStatus.Active &&
                    s.IAmAliveTime < beforeDate)
                .ToArrayAsync()
                .ConfigureAwait(false);

            if (silos.Length > 0)
            {
                ctx.Silos.RemoveRange(silos);
            }

            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Error cleaning up defunct silo entries");
            WrappedException.CreateAndRethrow(ex);
        }
    }

    public async Task<MembershipTableData> ReadRow(SiloAddress key)
    {
        try
        {
            var ctx = this._dbContextFactory.CreateDbContext();

            var record = await ctx.Silos.Include(s => s.Cluster).AsNoTracking()
                .SingleOrDefaultAsync(s =>
                    s.ClusterId == this._clusterId &&
                    s.Address == key.Endpoint.Address.ToString() &&
                    s.Port == key.Endpoint.Port &&
                    s.Generation == key.Generation)
                .ConfigureAwait(false);

            if (record is null)
            {
                throw new InvalidOperationException($"Silo '{key.ToParsableString()}' not found");
            }

            var version = new TableVersion(
                record.Cluster.Version,
                this._etagConverter.FromDbETag(record.Cluster.ETag)
            );

            var memEntries = new List<Tuple<MembershipEntry, string>> {Tuple.Create(ConvertRecord(record), this._etagConverter.FromDbETag(record.ETag))};

            return new MembershipTableData(memEntries, version);
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(exc, "Failure reading silo entry {Key} for cluster {Cluster}", key, _clusterId);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task<MembershipTableData> ReadAll()
    {
        try
        {
            var ctx = this._dbContextFactory.CreateDbContext();

            var clusterRecord = await ctx.Clusters.Include(s => s.Silos).AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == this._clusterId)
                .ConfigureAwait(false);

            if (clusterRecord is null)
            {
                throw new InvalidOperationException($"Cluster '{this._clusterId}' not found");
            }

            var version = new TableVersion(
                clusterRecord.Version,
                this._etagConverter.FromDbETag(clusterRecord.ETag)
            );

            var memEntries = new List<Tuple<MembershipEntry, string>>();
            foreach (var siloRecord in clusterRecord.Silos)
            {
                try
                {
                    var membershipEntry = ConvertRecord(siloRecord);
                    memEntries.Add(new Tuple<MembershipEntry, string>(membershipEntry, this._etagConverter.FromDbETag(siloRecord.ETag)));
                }
                catch (Exception exc)
                {
                    this._logger.LogError(exc, "Failure reading all membership records for cluster '{ClusterId}'", this._clusterId);
                    WrappedException.CreateAndRethrow(exc);
                    throw;
                }
            }

            return new MembershipTableData(memEntries, version);
        }
        catch (Exception exc)
        {
            _logger.LogWarning(exc, "Failure reading entries for cluster {Cluster}", this._clusterId);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
    {
        try
        {
            var clusterRecord = this.ConvertToRecord(tableVersion);
            var siloRecord = this.ConvertToRecord(entry);
            siloRecord.ClusterId = clusterRecord.Id;

            var ctx = this._dbContextFactory.CreateDbContext();

            ctx.Clusters.Update(clusterRecord);
            ctx.Silos.Add(siloRecord);
            var affected =await ctx.SaveChangesAsync().ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException exc)
        {
            this._logger.LogWarning(exc, "Failure inserting entry for cluster {Cluster}", this._clusterId);
            return false;
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(exc, "Failure inserting entry for cluster {Cluster}", this._clusterId);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
    {
        try
        {
            var clusterRecord = this.ConvertToRecord(tableVersion);
            var siloRecord = this.ConvertToRecord(entry);
            siloRecord.ETag = this._etagConverter.ToDbETag(etag);

            var ctx = this._dbContextFactory.CreateDbContext();

            ctx.Clusters.Update(clusterRecord);
            ctx.Silos.Update(siloRecord);

            var affected = await ctx.SaveChangesAsync().ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(exc, "Failure updating entry for cluster {Cluster}", this._clusterId);
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    public async Task UpdateIAmAlive(MembershipEntry entry)
    {
        var ctx = this._dbContextFactory.CreateDbContext();

        if (this._self is not { } selfRow)
        {
            var record = await ctx.Silos.AsNoTracking()
                .SingleOrDefaultAsync(s =>
                    s.ClusterId == this._clusterId &&
                    s.Address == entry.SiloAddress.Endpoint.Address.ToString() &&
                    s.Port == entry.SiloAddress.Endpoint.Port &&
                    s.Generation == entry.SiloAddress.Generation)
                .ConfigureAwait(false);

            if (record is null)
            {
                this._logger.LogWarning((int)ErrorCode.MembershipBase, "Unable to query silo {Silo}", entry.ToFullString());
                throw new OrleansException($"Unable to query silo {entry.ToFullString()}");
            }

            this._self = selfRow = record;
        }

        selfRow.IAmAliveTime = entry.IAmAliveTime;

        try
        {
            ctx.Silos.Update(selfRow);
            await ctx.SaveChangesAsync().ConfigureAwait(false);
            _self = selfRow;
        }
        catch (Exception exc)
        {
            _self = null;
            this._logger.LogWarning("Unable to update IAmAlive for Silo {Silo}", entry.ToFullString());
            WrappedException.CreateAndRethrow(exc);
            throw;
        }
    }

    private static MembershipEntry ConvertRecord(in SiloRecord<TETag> record)
    {
        var entry = new MembershipEntry
        {
            HostName = record.HostName,
            Status = record.Status,
            SiloName = record.Name,
            StartTime = record.StartTime.UtcDateTime,
            IAmAliveTime = record.IAmAliveTime.UtcDateTime
        };

        if (record.ProxyPort.HasValue)
        {
            entry.ProxyPort = record.ProxyPort.Value;
        }

        entry.SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Parse(record.Address), record.Port), record.Generation);

        var suspectingSilos = record.SuspectingSilos.Select(SiloAddress.FromParsableString).ToList();

        var suspectingTimes = record.SuspectingTimes.Select(LogFormatter.ParseDate).ToList();

        if (suspectingSilos.Count != suspectingTimes.Count)
        {
            throw new OrleansException($"SuspectingSilos.Length of {suspectingSilos.Count} as read from Azure Cosmos DB is not equal to SuspectingTimes.Length of {suspectingTimes.Count}");
        }

        for (var i = 0; i < suspectingSilos.Count; i++)
        {
            entry.AddSuspector(suspectingSilos[i], suspectingTimes[i]);
        }

        return entry;
    }

    private SiloRecord<TETag> ConvertToRecord(in MembershipEntry memEntry)
    {
        var record = new SiloRecord<TETag>
        {
            ClusterId = this._clusterId,
            Address = memEntry.SiloAddress.Endpoint.Address.ToString(),
            Port = memEntry.SiloAddress.Endpoint.Port,
            Generation = memEntry.SiloAddress.Generation,
            HostName = memEntry.HostName,
            Status = memEntry.Status,
            ProxyPort = memEntry.ProxyPort,
            Name = memEntry.SiloName,
            StartTime = memEntry.StartTime,
            IAmAliveTime = memEntry.IAmAliveTime
        };

        if (memEntry.SuspectTimes == null)
        {
            return record;
        }

        foreach (var tuple in memEntry.SuspectTimes)
        {
            record.SuspectingSilos.Add(tuple.Item1.ToParsableString());
            record.SuspectingTimes.Add(LogFormatter.PrintDate(tuple.Item2));
        }

        return record;
    }

    private ClusterRecord<TETag> ConvertToRecord(in TableVersion tableVersion)
    {
        return new() {Id = this._clusterId, Version = tableVersion.Version, Timestamp = DateTimeOffset.UtcNow, ETag = this._etagConverter.ToDbETag(tableVersion.VersionEtag)};
    }
}