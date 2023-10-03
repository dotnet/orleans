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
using Orleans.GrainDirectory.EntityFrameworkCore.Data;

namespace Orleans.GrainDirectory.EntityFrameworkCore;

public class EFCoreGrainDirectory<TDbContext> : IGrainDirectory, ILifecycleParticipant<ISiloLifecycle> where TDbContext : GrainDirectoryDbContext
{
    private readonly ILogger _logger;
    private readonly IDbContextFactory<TDbContext> _dbContextFactory;
    private readonly string _clusterId;

    public EFCoreGrainDirectory(
        ILoggerFactory loggerFactory,
        IDbContextFactory<TDbContext> dbContextFactory,
        IOptions<ClusterOptions> clusterOptions)
    {
        this._logger = loggerFactory.CreateLogger<EFCoreGrainDirectory<TDbContext>>();
        this._dbContextFactory = dbContextFactory;
        this._clusterId = clusterOptions.Value.ClusterId;
    }

    public Task<GrainAddress?> Register(GrainAddress address) => this.Register(address, null);

    public async Task<GrainAddress?> Register(GrainAddress address, GrainAddress? previousAddress)
    {
        var toRegister = this.FromGrainAddress(address);
        var grainIdStr = toRegister.GrainId;

        try
        {
            var ctx = this._dbContextFactory.CreateDbContext();

            if (previousAddress is not null)
            {
                var record = await ctx.Activations.AsNoTracking()
                    .SingleOrDefaultAsync(c =>
                        c.ClusterId == this._clusterId &&
                        c.GrainId == grainIdStr)
                    .ConfigureAwait(false);

                var previousRecord = this.FromGrainAddress(previousAddress);

                if (record is null)
                {
                    ctx.Activations.Add(toRegister);
                    await ctx.SaveChangesAsync().ConfigureAwait(false);
                }
                else if (record.ActivationId != previousRecord.ActivationId || record.SiloAddress != previousRecord.SiloAddress)
                {
                    return await Lookup(address.GrainId).ConfigureAwait(false);
                }
                else
                {
                    toRegister.ETag = record.ETag;

                    ctx.Activations.Update(toRegister);
                    await ctx.SaveChangesAsync().ConfigureAwait(false);

                    return address;
                }
            }
            else
            {
                ctx.Activations.Add(toRegister);
                await ctx.SaveChangesAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(exc, "Unable to update Grain Directory");
            WrappedException.CreateAndRethrow(exc);
            throw;
        }

        return await Lookup(address.GrainId).ConfigureAwait(false);
    }

    public async Task Unregister(GrainAddress address)
    {
        try
        {
            var ctx = this._dbContextFactory.CreateDbContext();

            var grainIdStr = address.GrainId.ToString();
            var activationIdStr = address.ActivationId.ToParsableString();

            var record = await ctx.Activations
                .FirstOrDefaultAsync(r =>
                    r.ClusterId == this._clusterId &&
                    r.GrainId == grainIdStr &&
                    r.ActivationId == activationIdStr)
                .ConfigureAwait(false);

            if (record is null) return;

            ctx.Activations.Remove(record);
            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(exc, "Unable to unregister activation");
        }
    }

    public async Task<GrainAddress?> Lookup(GrainId grainId)
    {
        try
        {
            var ctx = this._dbContextFactory.CreateDbContext();

            var grainIdStr = grainId.ToString();
            var record = await ctx.Activations.AsNoTracking()
                .FirstOrDefaultAsync(r =>
                    r.ClusterId == this._clusterId &&
                    r.GrainId == grainIdStr)
                .ConfigureAwait(false);

            return record is null ? default! : this.ToGrainAddress(record);
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(exc, "Unable to lookup Grain Directory");
            return default!;
        }
    }

    public async Task UnregisterSilos(List<SiloAddress> siloAddresses)
    {
        try
        {
            var ctx = this._dbContextFactory.CreateDbContext();

            var silos = siloAddresses.Select(s => s.ToParsableString()).ToArray();

            var records = await ctx.Activations.Where(r =>
                    silos.Contains(r.SiloAddress) &&
                    r.ClusterId == this._clusterId)
                .ToArrayAsync()
                .ConfigureAwait(false);

            ctx.Activations.RemoveRange(records);
            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(exc, "Unable to unregister silos from the Grain Directory");
        }
    }

    public async Task UnregisterMany(List<GrainAddress> addresses)
    {
        try
        {
            var ctx = this._dbContextFactory.CreateDbContext();

            foreach (var addr in addresses)
            {
                var grainId = addr.GrainId.ToString();
                var activationId = addr.ActivationId.ToParsableString();

                var records = await ctx.Activations
                    .Where(r => r.ClusterId == this._clusterId && grainId == r.GrainId && activationId == r.ActivationId)
                    .ToArrayAsync()
                    .ConfigureAwait(false);

                ctx.Activations.RemoveRange(records);
            }

            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception exc)
        {
            this._logger.LogWarning(exc, "Unable to unregister silos from the Grain Directory");
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
        lifecycle.Subscribe(nameof(EFCoreGrainDirectory<TDbContext>), ServiceLifecycleStage.RuntimeInitialize, InitializeIfNeeded);
    }

    public GrainAddress ToGrainAddress(GrainActivationRecord record)
    {
        return new GrainAddress {GrainId = GrainId.Parse(record.GrainId), SiloAddress = SiloAddress.FromParsableString(record.SiloAddress), ActivationId = ActivationId.FromParsableString(record.ActivationId), MembershipVersion = new MembershipVersion(record.MembershipVersion)};
    }

    private GrainActivationRecord FromGrainAddress(GrainAddress address)
    {
        ArgumentNullException.ThrowIfNull(address.SiloAddress);

        return new GrainActivationRecord
        {
            ClusterId = this._clusterId,
            GrainId = address.GrainId.ToString(),
            SiloAddress = address.SiloAddress.ToParsableString(),
            ActivationId = address.ActivationId.ToParsableString(),
            MembershipVersion = address.MembershipVersion.Value,
        };
    }
}