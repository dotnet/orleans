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
        private readonly IStreamCheckpointerGrain _grain;
        private readonly GrainStreamQueueCheckpointerOptions _options;
        private readonly object _lock = new();

        private string _latestCheckpoint = string.Empty;
        private string _persistedCheckpoint = string.Empty;
        private Task _inProgressSave = Task.CompletedTask;
        private DateTime? _throttleSavesUntilUtc;

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

            _grain = grain;
            _options = options;
        }

        /// <inheritdoc />
        public bool CheckpointExists
        {
            get
            {
                lock (_lock)
                {
                    return !string.IsNullOrEmpty(_latestCheckpoint);
                }
            }
        }

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
        public async Task<string> Load(CancellationToken cancellationToken)
        {
            var checkpoint = await _grain.Load(cancellationToken);
            lock (_lock)
            {
                _latestCheckpoint = checkpoint;
                _persistedCheckpoint = checkpoint;
            }

            return checkpoint;
        }

        /// <inheritdoc />
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        public Task Reset() => Reset(CancellationToken.None);

        /// <inheritdoc />
        public async Task Reset(CancellationToken cancellationToken)
        {
            Task resetTask;
            lock (_lock)
            {
                var inProgressSave = _inProgressSave;
                _latestCheckpoint = string.Empty;
                _throttleSavesUntilUtc = DateTime.MaxValue;
                resetTask = _inProgressSave = ResetCore(inProgressSave, cancellationToken);
            }

            try
            {
                await resetTask;
            }
            catch
            {
                lock (_lock)
                {
                    if (ReferenceEquals(resetTask, _inProgressSave))
                    {
                        _latestCheckpoint = _persistedCheckpoint;
                        _throttleSavesUntilUtc = null;
                        _inProgressSave = Task.CompletedTask;
                    }
                }

                throw;
            }

            lock (_lock)
            {
                if (ReferenceEquals(resetTask, _inProgressSave))
                {
                    _latestCheckpoint = string.Empty;
                    _persistedCheckpoint = string.Empty;
                    _throttleSavesUntilUtc = null;
                    _inProgressSave = Task.CompletedTask;
                }
            }
        }

        /// <inheritdoc />
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        public void Update(string offset, DateTime utcNow)
            => Update(offset, utcNow, CancellationToken.None);

        /// <inheritdoc />
        public void Update(string offset, DateTime utcNow, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(offset);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_lock)
            {
                if (string.Equals(_latestCheckpoint, offset, StringComparison.Ordinal)
                    || (_options.CheckpointComparer is { } comparer
                        && !string.IsNullOrEmpty(_latestCheckpoint)
                        && comparer.Compare(offset, _latestCheckpoint) <= 0))
                {
                    return;
                }

                _latestCheckpoint = offset;
                if (_throttleSavesUntilUtc.HasValue && (_throttleSavesUntilUtc.Value > utcNow || !_inProgressSave.IsCompleted))
                {
                    return;
                }

                _throttleSavesUntilUtc = utcNow + _options.PersistInterval;
                _inProgressSave = Save(offset, cancellationToken);
                _inProgressSave.Ignore();
            }
        }

        /// <inheritdoc />
        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            var retryingSave = false;
            while (true)
            {
                Task inProgressSave;
                lock (_lock)
                {
                    inProgressSave = _inProgressSave;
                }

                if (retryingSave)
                {
                    await inProgressSave.WaitAsync(cancellationToken);
                }
                else
                {
                    try
                    {
                        await inProgressSave.WaitAsync(cancellationToken);
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested)
                    {
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }

                lock (_lock)
                {
                    if (!ReferenceEquals(inProgressSave, _inProgressSave))
                    {
                        retryingSave = false;
                        continue;
                    }

                    if (string.Equals(_persistedCheckpoint, _latestCheckpoint, StringComparison.Ordinal))
                    {
                        return;
                    }

                    _inProgressSave = Save(_latestCheckpoint, cancellationToken);
                    retryingSave = true;
                }
            }
        }

        private async Task Save(string checkpoint, CancellationToken cancellationToken)
        {
            string expectedCheckpoint;
            lock (_lock)
            {
                expectedCheckpoint = _persistedCheckpoint;
            }

            while (true)
            {
                var persistedCheckpoint = await _grain.Update(
                    checkpoint,
                    expectedCheckpoint,
                    cancellationToken);

                lock (_lock)
                {
                    _persistedCheckpoint = persistedCheckpoint;
                    if (string.Equals(persistedCheckpoint, checkpoint, StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (_options.CheckpointComparer is not { } comparer)
                    {
                        _latestCheckpoint = persistedCheckpoint;
                        return;
                    }

                    if (comparer.Compare(_latestCheckpoint, persistedCheckpoint) <= 0)
                    {
                        _latestCheckpoint = persistedCheckpoint;
                    }

                    if (comparer.Compare(checkpoint, persistedCheckpoint) <= 0)
                    {
                        return;
                    }

                    expectedCheckpoint = persistedCheckpoint;
                }
            }
        }

        private async Task ResetCore(Task inProgressSave, CancellationToken cancellationToken)
        {
            await inProgressSave.WaitAsync(cancellationToken);

            string expectedCheckpoint;
            lock (_lock)
            {
                expectedCheckpoint = _persistedCheckpoint;
            }

            while (true)
            {
                var persistedCheckpoint = await _grain.Update(
                    string.Empty,
                    expectedCheckpoint,
                    cancellationToken);
                if (string.IsNullOrEmpty(persistedCheckpoint))
                {
                    return;
                }

                expectedCheckpoint = persistedCheckpoint;
            }
        }
    }
}
