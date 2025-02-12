using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Persistence.Migration
{
    public class MigrationGrainStorage : IGrainStorage
    {
        [Serializable]
        private struct MigrationEtag
        {
            public string SourceETag { get; set; }
            public string DestinationETag { get; set; }

            public MigrationEtag(string sourceETag, string destinationETag)
            {
                this.SourceETag = sourceETag;
                this.DestinationETag = destinationETag;
            }

            public string SerializeToJson() => JsonConvert.SerializeObject(this, typeof(MigrationEtag), null);

            public static string SerializeToJson(string sourceETag, string destinationETag)
            {
                var obj = new MigrationEtag(sourceETag, destinationETag);
                return JsonConvert.SerializeObject(obj);
            }

            public static MigrationEtag ParseFromJson(string json) => (MigrationEtag)JsonConvert.DeserializeObject(json, typeof(MigrationEtag));
        }

        private readonly IExtendedGrainStorage _extendedSourceStorage;
        private readonly bool _saveMigrationMetadata;

        private readonly IGrainStorage _sourceStorage;
        private readonly IGrainStorage _destinationStorage;

        private readonly MigrationGrainStorageOptions _options;
        private readonly ILogger<MigrationGrainStorage> _logger;

        public MigrationGrainStorage(
            IGrainStorage sourceStorage,
            IGrainStorage destinationStorage,
            MigrationGrainStorageOptions options,
            ILogger<MigrationGrainStorage> logger)
        {
            _options = options;
            _sourceStorage = sourceStorage;
            _destinationStorage = destinationStorage;

            _logger = logger;
            _extendedSourceStorage = sourceStorage as IExtendedGrainStorage;
            _saveMigrationMetadata = _extendedSourceStorage is not null && options.SaveMigrationMetadata;
        }

        public async Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            var eTag = MigrationEtag.ParseFromJson(grainState.ETag);
            grainState.ETag = eTag.SourceETag;
            await _sourceStorage.ClearStateAsync(grainType, grainReference, grainState);
            grainState.ETag = eTag.DestinationETag;
            await _destinationStorage.ClearStateAsync(grainType, grainReference, grainState);
        }

        public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            await _destinationStorage.ReadStateAsync(grainType, grainReference, grainState);
            if (grainState.RecordExists)
            {
                grainState.ETag = MigrationEtag.SerializeToJson(null, grainState.ETag);
            }
            else
            {
                await _sourceStorage.ReadStateAsync(grainType, grainReference, grainState);
                grainState.ETag = MigrationEtag.SerializeToJson(grainState.ETag, null);
            }
        }

        public async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            var etag = new MigrationEtag();
            if (grainState.RecordExists)
            {
                etag = MigrationEtag.ParseFromJson(grainState.ETag);
            }

            try
            {
                // destination storage
                grainState.ETag = grainState.RecordExists ? etag.DestinationETag : default;
                await _destinationStorage.WriteStateAsync(grainType, grainReference, grainState);
                etag.DestinationETag = grainState.ETag;

                StorageEntry? storageEntry = null;
                if (!_options.WriteToDestinationOnly) // enabled writing to source storage as well
                {
                    grainState.ETag = grainState.RecordExists ? etag.SourceETag : default;
                    if (_saveMigrationMetadata)
                    {
                        // if we want to save metadata about migration, then using extended storage API to get the storageEntry back
                        // and then mark entity as migrated
                        storageEntry = await _extendedSourceStorage.WriteStateWithEntryAsync(grainType, grainReference, grainState);
                    }
                    else
                    {
                        await _sourceStorage.WriteStateAsync(grainType, grainReference, grainState);
                    }
                    etag.SourceETag = grainState.ETag;
                }

                // mark entity as migrated only after both writes (to source and destination) were successful
                try
                {
                    if (_saveMigrationMetadata)
                    {
                        storageEntry ??= await _extendedSourceStorage.GetStorageEntryAsync(grainType, grainReference, grainState);

                        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        var updatedETag = await storageEntry.Value.MigrationEntryClient.MarkMigratedAsync(cts.Token);
                        if (updatedETag is not null)
                        {
                            etag.SourceETag = updatedETag; // override the ETag with one from the storageEntry
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.Warn((int)MigrationErrorCodes.MigrationMetadataNotWritten, $"failed to save migration metadata for grain (type = {grainType}, id = {grainReference.GrainId})", ex);
                    }
                    
                    // swallow the exception, since we don't want to affect the grain state write with this operation
                }
            }
            finally
            {
                grainState.ETag = etag.SerializeToJson();
            }
        }

        public static IGrainStorage Create(IServiceProvider serviceProvider, string name)
        {
            var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<MigrationGrainStorage>();
            var options = serviceProvider.GetRequiredService<IOptionsMonitor<MigrationGrainStorageOptions>>().Get(name);

            var source = serviceProvider.GetRequiredServiceByName<IGrainStorage>(options.SourceStorageName);
            var destination = serviceProvider.GetRequiredServiceByName<IGrainStorage>(options.DestinationStorageName);

            return new MigrationGrainStorage(source, destination, options, logger);
        }
    }

    /// <summary>
    /// Configuration to control migration grain storage behavior
    /// </summary>
    public class MigrationGrainStorageOptions
    {
        public string SourceStorageName { get; set; }

        public string DestinationStorageName { get; set; }

        /// <summary>
        /// When true, will only write to the destination storage (not to source storage).
        /// False by default (writes to both source and destination). <br/>
        /// Should be enabled in later stages of migration (when the source storage is already a fallback option).
        /// </summary>
        public bool WriteToDestinationOnly { get; set; } = false;

        /// <summary>
        /// If enabled, will also save the metadata of migration process in the source storage.
        /// For example, it will persistently keep the migrationTime of the specific storage entry. <br/>
        /// </summary>
        public bool SaveMigrationMetadata { get; set; } = false;
    }
}
