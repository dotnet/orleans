namespace Orleans.GrainDirectory.AdoNet;

internal sealed partial class AdoNetGrainDirectory(string name, AdoNetGrainDirectoryOptions options, ILogger<AdoNetGrainDirectory> logger, IOptions<ClusterOptions> clusterOptions, IHostApplicationLifetime lifetime) : IGrainDirectory
{
    private readonly ILogger _logger = logger;
    private readonly string _clusterId = clusterOptions.Value.ClusterId;
    private RelationalOrleansQueries? _queries;

    /// <summary>
    /// Looks up a grain activation.
    /// </summary>
    /// <param name="grainId">The grain identifier.</param>
    /// <returns>The grain address if found or null if not found.</returns>
    public async Task<GrainAddress?> Lookup(GrainId grainId)
    {
        try
        {
            var queries = await GetQueriesAsync();

            var entry = await queries
                .LookupGrainActivationAsync(_clusterId, name, grainId.ToString())
                .WaitAsync(lifetime.ApplicationStopping);

            return entry?.ToGrainAddress();
        }
        catch (Exception ex)
        {
            LogFailedToLookup(ex, _clusterId, grainId);
            throw;
        }
    }

    /// <summary>
    /// Registers a new grain activation.
    /// </summary>
    /// <param name="address">The grain address.</param>
    /// <returns>The new or current grain address.</returns>
    public async Task<GrainAddress?> Register(GrainAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(address.SiloAddress);

        try
        {
            var queries = await GetQueriesAsync();

            // this call is expected to register a new entry or return the existing one if found in a thread safe manner
            var entry = await queries
                .RegisterGrainActivationAsync(_clusterId, name, address.GrainId.ToString(), address.SiloAddress.ToParsableString(), address.ActivationId.ToParsableString())
                .WaitAsync(lifetime.ApplicationStopping);

            LogRegistered(_clusterId, address.GrainId, address.SiloAddress, address.ActivationId);

            return entry.ToGrainAddress();
        }
        catch (Exception ex)
        {
            LogFailedToRegister(ex, _clusterId, address.GrainId, address.SiloAddress, address.ActivationId);
            throw;
        }
    }

    /// <summary>
    /// Unregister an existing grain activation.
    /// </summary>
    /// <param name="address">The grain address.</param>
    public async Task Unregister(GrainAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(address.SiloAddress);

        try
        {
            var queries = await GetQueriesAsync();

            var count = await queries
                .UnregisterGrainActivationAsync(_clusterId, name, address.GrainId.ToString(), address.ActivationId.ToParsableString())
                .WaitAsync(lifetime.ApplicationStopping);

            if (count > 0)
            {
                LogUnregistered(_clusterId, address.GrainId, address.SiloAddress, address.ActivationId);
            }
        }
        catch (Exception ex)
        {
            LogFailedToUnregister(ex, _clusterId, address.GrainId, address.SiloAddress, address.ActivationId);
            throw;
        }
    }

    /// <summary>
    /// Unregisters all grain activations in the specified set of silos.
    /// </summary>
    /// <param name="siloAddresses">The set of silos.</param>
    public async Task UnregisterSilos(List<SiloAddress> siloAddresses)
    {
        ArgumentNullException.ThrowIfNull(siloAddresses);

        if (siloAddresses.Count == 0)
        {
            return;
        }

        try
        {
            var queries = await GetQueriesAsync();

            var count = await queries
                .UnregisterGrainActivationsAsync(_clusterId, name, GetSilosAddressesAsString(siloAddresses))
                .WaitAsync(lifetime.ApplicationStopping);

            if (count > 0)
            {
                LogUnregisteredSilos(count, _clusterId, siloAddresses);
            }
        }
        catch (Exception ex)
        {
            LogFailedToUnregisterSilos(ex, _clusterId, siloAddresses);
            throw;
        }

        static string GetSilosAddressesAsString(IEnumerable<SiloAddress> siloAddresses) => string.Join('|', siloAddresses.Select(x => x.ToParsableString()));
    }

    /// <summary>
    /// Unfortunate implementation detail to account for lack of async lifetime.
    /// Ideally this concern will be moved upstream so this won't be needed.
    /// </summary>
    private readonly SemaphoreSlim _semaphore = new(1);

    /// <summary>
    /// Ensures queries are loaded only once while allowing for recovery if the load fails.
    /// </summary>
    private ValueTask<RelationalOrleansQueries> GetQueriesAsync()
    {
        // attempt fast path
        return _queries is not null ? new(_queries) : new(CoreAsync());

        // slow path
        async Task<RelationalOrleansQueries> CoreAsync()
        {
            await _semaphore.WaitAsync(lifetime.ApplicationStopping);
            try
            {
                // attempt fast path again
                if (_queries is not null)
                {
                    return _queries;
                }

                // slow path - the member variable will only be set if the call succeeds
                return _queries = await RelationalOrleansQueries
                    .CreateInstance(options.Invariant, options.ConnectionString)
                    .WaitAsync(lifetime.ApplicationStopping);
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }

    #region Logging

    [LoggerMessage(1, LogLevel.Error, "Failed to lookup({ClusterId}, {GrainId})")]
    private partial void LogFailedToLookup(Exception ex, string clusterId, GrainId grainId);

    [LoggerMessage(2, LogLevel.Debug, "Registered ({ClusterId}, {GrainId}, {SiloAddress}, {ActivationId})")]
    private partial void LogRegistered(string clusterId, GrainId grainId, SiloAddress siloAddress, ActivationId activationId);

    [LoggerMessage(3, LogLevel.Error, "Failed to register ({ClusterId}, {GrainId}, {SiloAddress}, {ActivationId})")]
    private partial void LogFailedToRegister(Exception ex, string clusterId, GrainId grainId, SiloAddress siloAddress, ActivationId activationId);

    [LoggerMessage(4, LogLevel.Debug, "Unregistered ({ClusterId}, {GrainId}, {SiloAddress}, {ActivationId})")]
    private partial void LogUnregistered(string clusterId, GrainId grainId, SiloAddress siloAddress, ActivationId activationId);

    [LoggerMessage(5, LogLevel.Error, "Failed to unregister ({ClusterId}, {GrainId}, {SiloAddress}, {ActivationId})")]
    private partial void LogFailedToUnregister(Exception ex, string clusterId, GrainId grainId, SiloAddress siloAddress, ActivationId activationId);

    [LoggerMessage(6, LogLevel.Debug, "Unregistered {Count} activations from silos {SiloAddresses} in cluster {ClusterId}")]
    private partial void LogUnregisteredSilos(int count, string clusterId, IEnumerable<SiloAddress> siloAddresses);

    [LoggerMessage(7, LogLevel.Error, "Failed to unregister silos {SiloAddresses} in cluster {ClusterId}")]
    private partial void LogFailedToUnregisterSilos(Exception ex, string clusterId, IEnumerable<SiloAddress> siloAddresses);

    #endregion Logging
}
