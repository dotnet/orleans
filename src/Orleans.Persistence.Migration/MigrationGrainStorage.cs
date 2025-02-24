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
        }

        public async Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            var eTag = MigrationEtag.ParseFromJson(grainState.ETag);
            switch (_options.Mode)
            {
                case GrainMigrationMode.Disabled:
                {
                    grainState.ETag = eTag.SourceETag;
                    await _sourceStorage.ClearStateAsync(grainType, grainReference, grainState);
                    break;
                }

                case GrainMigrationMode.ReadWriteDestination:
                {
                    grainState.ETag = eTag.DestinationETag;
                    await _destinationStorage.ClearStateAsync(grainType, grainReference, grainState);
                    break;
                }

                case GrainMigrationMode.ReadSource_WriteBoth:
                case GrainMigrationMode.ReadDestinationWithFallback_WriteBoth:
                {
                    grainState.ETag = eTag.SourceETag;
                    await _sourceStorage.ClearStateAsync(grainType, grainReference, grainState);
                    grainState.ETag = eTag.DestinationETag;
                    await _destinationStorage.ClearStateAsync(grainType, grainReference, grainState);

                    break;
                }

                default: throw new ArgumentOutOfRangeException(nameof(_options.Mode));
            }
        }

        public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            switch (_options.Mode)
            {
                case GrainMigrationMode.Disabled:
                case GrainMigrationMode.ReadSource_WriteBoth:
                {
                    await _sourceStorage.ReadStateAsync(grainType, grainReference, grainState);
                    grainState.ETag = MigrationEtag.SerializeToJson(grainState.ETag, null);
                    break;
                }

                case GrainMigrationMode.ReadWriteDestination:
                {
                    await _destinationStorage.ReadStateAsync(grainType, grainReference, grainState);
                    grainState.ETag = MigrationEtag.SerializeToJson(null, grainState.ETag);
                    break;
                }

                case GrainMigrationMode.ReadDestinationWithFallback_WriteBoth:
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

                    break;
                }

                default: throw new ArgumentOutOfRangeException(nameof(_options.Mode));
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
                switch (_options.Mode)
                {
                    case GrainMigrationMode.Disabled:
                    {
                        grainState.ETag = grainState.RecordExists ? etag.SourceETag : default;
                        await _sourceStorage.WriteStateAsync(grainType, grainReference, grainState);
                        etag.SourceETag = grainState.ETag;
                        break;
                    }

                    case GrainMigrationMode.ReadWriteDestination:
                    {
                        grainState.ETag = grainState.RecordExists ? etag.DestinationETag : default;
                        await _destinationStorage.WriteStateAsync(grainType, grainReference, grainState);
                        etag.DestinationETag = grainState.ETag;
                        break;
                    }

                    case GrainMigrationMode.ReadSource_WriteBoth:
                    case GrainMigrationMode.ReadDestinationWithFallback_WriteBoth:
                    {
                        grainState.ETag = grainState.RecordExists ? etag.SourceETag : default;
                        await _sourceStorage.WriteStateAsync(grainType, grainReference, grainState);
                        etag.SourceETag = grainState.ETag;

                        // destination storage
                        grainState.ETag = grainState.RecordExists ? etag.DestinationETag : default;
                        await _destinationStorage.WriteStateAsync(grainType, grainReference, grainState);
                        etag.DestinationETag = grainState.ETag;

                        break;
                    }

                    default: throw new ArgumentOutOfRangeException(nameof(_options.Mode));
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
        /// Controls the way migration for grains is happening
        /// </summary>
        public GrainMigrationMode Mode { get; set; } = GrainMigrationMode.Disabled;
    }

    public enum GrainMigrationMode
    {
        /// <summary>
        /// Migration grain storage is completely disabled.
        /// Only source storage will be used for read/write and clear operations.
        /// </summary>
        Disabled = 0,

        /// <summary>
        /// Reading would happen from source storage only, and writes will target both source and destination storages.
        /// <br/>
        /// <i>Should be used as a first step of migration.</i>
        /// </summary>
        ReadSource_WriteBoth = 1,

        /// <summary>
        /// Reading would happen from destination storage, and if entry does not exist, will also check source storage.
        /// Write will target both storages.
        /// <br/>
        /// <i>Should be used as latter step of migration.</i>
        /// </summary>
        ReadDestinationWithFallback_WriteBoth = 2,

        /// <summary>
        /// Reading and writing happens only against destination storage
        /// </summary>
        ReadWriteDestination = 3
    }
}
