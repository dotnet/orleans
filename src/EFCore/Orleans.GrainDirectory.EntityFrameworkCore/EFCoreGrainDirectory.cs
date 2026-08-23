using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Configuration;
using Orleans.EntityFrameworkCore;
using Orleans.GrainDirectory.EntityFrameworkCore.Data;

namespace Orleans.GrainDirectory.EntityFrameworkCore;

public class EFCoreGrainDirectory<TDbContext, TETag> : IGrainDirectory, ILifecycleParticipant<ISiloLifecycle> where TDbContext : GrainDirectoryDbContext<TDbContext, TETag>
{
    private readonly ILogger _logger;
    private readonly IDbContextFactory<TDbContext> _dbContextFactory;
    private readonly IEFGrainDirectoryETagConverter<TETag> _eTagConverter;
    private readonly string _clusterId;
    private readonly byte[] _clusterIdHash;

    public EFCoreGrainDirectory(
        ILoggerFactory loggerFactory,
        IDbContextFactory<TDbContext> dbContextFactory,
        IOptions<ClusterOptions> clusterOptions,
        IEFGrainDirectoryETagConverter<TETag> eTagConverter)
    {
        this._logger = loggerFactory.CreateLogger<EFCoreGrainDirectory<TDbContext, TETag>>();
        this._dbContextFactory = dbContextFactory;
        this._clusterId = clusterOptions.Value.ClusterId;
        this._clusterIdHash = EFCoreIdentifierHash.Compute(this._clusterId);
        this._eTagConverter = eTagConverter;
    }

    public Task<GrainAddress?> Register(GrainAddress address) => this.Register(address, null);

    public async Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress)
    {
        var toRegister = this.FromGrainAddress(address);
        var grainIdStr = toRegister.GrainId;
        var grainIdHash = toRegister.GrainIdHash;

        try
        {
            await using var ctx = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

            if (previousAddress is not null)
            {
                var candidates = await ctx.Activations.AsNoTracking()
                    .Where(c => c.ClusterIdHash == this._clusterIdHash && c.GrainIdHash == grainIdHash)
                    .ToArrayAsync()
                    .ConfigureAwait(false);
                var record = GetExactRecord(candidates, this._clusterId, grainIdStr);

                var previousEntry = this.FromGrainAddress(previousAddress);

                if (record is null)
                {
                    ctx.Activations.Add(toRegister);
                    await ctx.SaveChangesAsync().ConfigureAwait(false);
                }
                else if (!string.Equals(record.ActivationId, previousEntry.ActivationId, StringComparison.Ordinal) ||
                    !string.Equals(record.SiloAddress, previousEntry.SiloAddress, StringComparison.Ordinal))
                {
                    return await Lookup(address.GrainId).ConfigureAwait(false);
                }
                else
                {
                    toRegister.ETag = record.ETag;

                    ctx.Activations.Update(toRegister);
                    await ctx.SaveChangesAsync().ConfigureAwait(false);

                    return this.ToGrainAddress(toRegister);
                }
            }
            else
            {
                ctx.Activations.Add(toRegister);
                await ctx.SaveChangesAsync().ConfigureAwait(false);
            }
        }
        catch (DbUpdateException exception)
        {
            this._logger.LogDebug(exception, "Possible concurrent registration for grain {GrainId}", address.GrainId);
            var winner = await Lookup(address.GrainId).ConfigureAwait(false);
            if (winner is not null)
            {
                return winner;
            }

            throw;
        }

        return await Lookup(address.GrainId).ConfigureAwait(false);
    }

    public async Task Unregister(GrainAddress address)
    {
        try
        {
            await using var ctx = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

            var grainIdStr = address.GrainId.ToString();
            var grainIdHash = EFCoreIdentifierHash.Compute(grainIdStr);
            var activationIdStr = address.ActivationId.ToParsableString();

            var candidates = await ctx.Activations
                .Where(r => r.ClusterIdHash == this._clusterIdHash && r.GrainIdHash == grainIdHash)
                .ToArrayAsync()
                .ConfigureAwait(false);
            var record = GetExactRecord(candidates, this._clusterId, grainIdStr);
            if (record is not null &&
                !string.Equals(record.ActivationId, activationIdStr, StringComparison.Ordinal))
            {
                record = null;
            }

            if (record is null) return;

            ctx.Activations.Remove(record);
            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(exc, "Unable to unregister activation");
            throw;
        }
    }

    public async Task<GrainAddress?> Lookup(GrainId grainId)
    {
        try
        {
            await using var ctx = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

            var grainIdStr = grainId.ToString();
            var grainIdHash = EFCoreIdentifierHash.Compute(grainIdStr);
            var candidates = await ctx.Activations.AsNoTracking()
                .Where(r => r.ClusterIdHash == this._clusterIdHash && r.GrainIdHash == grainIdHash)
                .ToArrayAsync()
                .ConfigureAwait(false);
            var record = GetExactRecord(candidates, this._clusterId, grainIdStr);

            return record is null ? default! : this.ToGrainAddress(record);
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(exc, "Unable to lookup Grain Directory");
            throw;
        }
    }

    public async Task UnregisterSilos(List<SiloAddress> siloAddresses)
    {
        try
        {
            await using var ctx = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

            var silos = siloAddresses.Select(s => s.ToParsableString()).ToArray();
            if (silos.Length == 0)
            {
                return;
            }

            var siloIdentifiers = silos.ToHashSet(StringComparer.Ordinal);
            var candidates = await ctx.Activations
                .Where(record => record.ClusterIdHash == this._clusterIdHash)
                .ToArrayAsync()
                .ConfigureAwait(false);
            if (candidates.Any(record =>
                !string.Equals(record.ClusterId, this._clusterId, StringComparison.Ordinal)))
            {
                throw CreateHashCollisionException();
            }

            var records = candidates
                .Where(record => siloIdentifiers.Contains(record.SiloAddress))
                .ToArray();

            ctx.Activations.RemoveRange(records);
            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(exc, "Unable to unregister silos from the Grain Directory");
            throw;
        }
    }

    public async Task UnregisterMany(List<GrainAddress> addresses)
    {
        try
        {
            await using var ctx = await this._dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

            if (addresses.Count == 0)
            {
                return;
            }

            var identifiers = addresses
                .Select(address =>
                {
                    var grainId = address.GrainId.ToString();
                    return (GrainId: grainId, ActivationId: address.ActivationId.ToParsableString(), GrainIdHash: EFCoreIdentifierHash.Compute(grainId));
                })
                .ToArray();
            var candidates = await ctx.Activations
                .Where(record => record.ClusterIdHash == this._clusterIdHash)
                .ToArrayAsync()
                .ConfigureAwait(false);
            if (candidates.Any(record =>
                !string.Equals(record.ClusterId, this._clusterId, StringComparison.Ordinal)))
            {
                throw CreateHashCollisionException();
            }

            var records = candidates.Where(record =>
                    identifiers.Any(identifier =>
                        string.Equals(record.GrainId, identifier.GrainId, StringComparison.Ordinal) &&
                        string.Equals(record.ActivationId, identifier.ActivationId, StringComparison.Ordinal)))
                .ToArray();
            ctx.Activations.RemoveRange(records);
            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(exc, "Unable to unregister silos from the Grain Directory");
            throw;
        }
    }

    private Task InitializeIfNeeded(CancellationToken ct = default)
    {
        if (this._logger.IsEnabled(LogLevel.Debug))
        {
            this._logger.LogDebug("Grain Directory initialized!");
        }

        return Task.CompletedTask;
    }

    public void Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(nameof(EFCoreGrainDirectory<TDbContext, TETag>), ServiceLifecycleStage.RuntimeInitialize, InitializeIfNeeded);
    }

    public GrainAddress ToGrainAddress(GrainActivationRecord<TETag> record)
    {
        return new GrainAddress {GrainId = GrainId.Parse(record.GrainId), SiloAddress = SiloAddress.FromParsableString(record.SiloAddress), ActivationId = ActivationId.FromParsableString(record.ActivationId), MembershipVersion = new MembershipVersion(record.MembershipVersion)};
    }

    private GrainActivationRecord<TETag> FromGrainAddress(GrainAddress address)
    {
        ArgumentNullException.ThrowIfNull(address.SiloAddress);

        return new GrainActivationRecord<TETag>
        {
            ClusterIdHash = this._clusterIdHash,
            GrainIdHash = EFCoreIdentifierHash.Compute(address.GrainId.ToString()),
            SiloAddressHash = EFCoreIdentifierHash.Compute(address.SiloAddress.ToParsableString()),
            ClusterId = this._clusterId,
            GrainId = address.GrainId.ToString(),
            SiloAddress = address.SiloAddress.ToParsableString(),
            ActivationId = address.ActivationId.ToParsableString(),
            MembershipVersion = address.MembershipVersion.Value,
        };
    }

    private static GrainActivationRecord<TETag>? GetExactRecord(
        GrainActivationRecord<TETag>[] candidates,
        string clusterId,
        string grainId)
    {
        foreach (var candidate in candidates)
        {
            if (!string.Equals(candidate.ClusterId, clusterId, StringComparison.Ordinal) ||
                !string.Equals(candidate.GrainId, grainId, StringComparison.Ordinal))
            {
                throw CreateHashCollisionException();
            }
        }

        return candidates.SingleOrDefault();
    }

    private static InvalidOperationException CreateHashCollisionException() =>
        new("An Entity Framework Core grain directory identifier hash collision was detected.");
}