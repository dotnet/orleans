using System;
using System.Text;
using System.Threading.Tasks;
using Orleans.Configuration;
using Orleans.Providers;

namespace Orleans.Streams
{
    /// <summary>
    /// Persists stream queue checkpoints using Orleans grains.
    /// </summary>
    public class GrainStreamQueueCheckpointer : IStreamQueueCheckpointer<string>
    {
        private const char KeySeparator = '-';
        private const string StorageProviderKeyPrefix = "__orleans_storage_provider__-";
        private readonly StreamQueueCheckpointer _inner;

        /// <summary>
        /// Initializes a new instance with default options.
        /// </summary>
        /// <param name="grain">The grain used to persist checkpoints.</param>
        public GrainStreamQueueCheckpointer(IStreamCheckpointerGrain grain)
            : this(grain, new GrainStreamQueueCheckpointerOptions())
        {
        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="grain">The grain used to persist checkpoints.</param>
        /// <param name="options">The checkpointer options.</param>
        public GrainStreamQueueCheckpointer(IStreamCheckpointerGrain grain, GrainStreamQueueCheckpointerOptions options)
        {
            ArgumentNullException.ThrowIfNull(grain);
            ArgumentNullException.ThrowIfNull(options);
            if (options.PersistInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.PersistInterval,
                    $"{nameof(GrainStreamQueueCheckpointerOptions.PersistInterval)} must be greater than zero.");
            }

            if (string.IsNullOrWhiteSpace(options.StorageProviderName))
            {
                throw new ArgumentException(
                    $"{nameof(GrainStreamQueueCheckpointerOptions.StorageProviderName)} is required.",
                    nameof(options));
            }

            _inner = new StreamQueueCheckpointer(
                new StreamCheckpointStoreAdapter(grain),
                new StreamQueueCheckpointerOptions
                {
                    CheckpointComparer = options.CheckpointComparer,
                    PersistInterval = options.PersistInterval,
                });
        }

        /// <inheritdoc />
        public bool CheckpointExists => _inner.CheckpointExists;

        /// <summary>
        /// Creates and initializes a grain-based checkpointer with default options.
        /// </summary>
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        public static async Task<IStreamQueueCheckpointer<string>> Create(string providerName, string partition, string serviceId, IClusterClient clusterClient)
            => await Create(providerName, partition, serviceId, clusterClient, CancellationToken.None);

        /// <summary>
        /// Creates and initializes a grain-based checkpointer with default options.
        /// </summary>
        public static async Task<IStreamQueueCheckpointer<string>> Create(
            string providerName,
            string partition,
            string serviceId,
            IClusterClient clusterClient,
            CancellationToken cancellationToken)
        {
            return await Create(
                providerName,
                partition,
                serviceId,
                clusterClient,
                new GrainStreamQueueCheckpointerOptions(),
                cancellationToken);
        }

        /// <summary>
        /// Creates and initializes a grain-based checkpointer.
        /// </summary>
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        public static async Task<IStreamQueueCheckpointer<string>> Create(
            string providerName,
            string partition,
            string serviceId,
            IClusterClient clusterClient,
            GrainStreamQueueCheckpointerOptions options)
            => await Create(providerName, partition, serviceId, clusterClient, options, CancellationToken.None);

        /// <summary>
        /// Creates and initializes a grain-based checkpointer.
        /// </summary>
        public static async Task<IStreamQueueCheckpointer<string>> Create(
            string providerName,
            string partition,
            string serviceId,
            IClusterClient clusterClient,
            GrainStreamQueueCheckpointerOptions options,
            CancellationToken cancellationToken)
        {
            var grainKey = GetGrainKey(providerName, serviceId, partition, options.StorageProviderName);
            IStreamCheckpointerGrain grain = string.Equals(
                options.StorageProviderName,
                ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME,
                StringComparison.Ordinal)
                    ? clusterClient.GetGrain<IStreamCheckpointerGrain>(grainKey)
                    : clusterClient.GetGrain<IConfiguredStreamCheckpointerGrain>(grainKey);

            var checkpoint = new GrainStreamQueueCheckpointer(grain, options);
            _ = await checkpoint.Load(cancellationToken);

            return checkpoint;
        }

        internal static string GetGrainKey(
            string providerName,
            string serviceId,
            string partition,
            string storageProviderName)
        {
            var key = new StringBuilder();
            AppendKeyPart(key, serviceId);
            AppendKeyPart(key, providerName);
            AppendKeyPart(key, partition);
            if (string.Equals(
                storageProviderName,
                ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME,
                StringComparison.Ordinal))
            {
                return key.ToString();
            }

            var encodedStorageProvider = Convert.ToBase64String(Encoding.UTF8.GetBytes(storageProviderName));
            return $"{StorageProviderKeyPrefix}{encodedStorageProvider}{KeySeparator}{key}";
        }

        private static void AppendKeyPart(StringBuilder key, string value)
            => key.Append(value.Length).Append(KeySeparator).Append(value);

        internal static string GetConfiguredStorageProviderName(ReadOnlySpan<byte> grainKey)
        {
            var key = Encoding.UTF8.GetString(grainKey);
            if (!key.StartsWith(StorageProviderKeyPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The configured checkpoint grain key has an invalid format.");
            }

            var providerNameEnd = key.IndexOf(KeySeparator, StorageProviderKeyPrefix.Length);
            if (providerNameEnd < 0)
            {
                throw new InvalidOperationException("The checkpoint grain key contains an invalid storage provider name.");
            }

            var encodedStorageProvider = key[StorageProviderKeyPrefix.Length..providerNameEnd];
            return Encoding.UTF8.GetString(Convert.FromBase64String(encodedStorageProvider));
        }

        /// <inheritdoc />
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        public Task<string> Load() => Load(CancellationToken.None);

        /// <inheritdoc />
        public Task<string> Load(CancellationToken cancellationToken) => _inner.Load(cancellationToken);

        /// <inheritdoc />
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        public void Update(string offset, DateTime utcNow)
            => Update(offset, utcNow, CancellationToken.None);

        /// <inheritdoc />
        public void Update(string offset, DateTime utcNow, CancellationToken cancellationToken)
            => _inner.Update(offset, utcNow, cancellationToken);

        /// <inheritdoc />
        public Task FlushAsync(CancellationToken cancellationToken)
            => _inner.FlushAsync(cancellationToken);

        private sealed class StreamCheckpointStoreAdapter(IStreamCheckpointerGrain grain) : IStreamCheckpointStore
        {
            public async ValueTask<StreamCheckpointStoreState> Load(CancellationToken cancellationToken)
            {
                var checkpoint = await grain.Load(cancellationToken).ConfigureAwait(false);
                return new(checkpoint, checkpoint);
            }

            public async ValueTask<StreamCheckpointStoreState> Update(
                string checkpoint,
                string expectedVersion,
                CancellationToken cancellationToken)
            {
                var persistedCheckpoint = await grain.Update(
                    checkpoint,
                    expectedVersion,
                    cancellationToken).ConfigureAwait(false);
                return new(persistedCheckpoint, persistedCheckpoint);
            }
        }
    }
}
