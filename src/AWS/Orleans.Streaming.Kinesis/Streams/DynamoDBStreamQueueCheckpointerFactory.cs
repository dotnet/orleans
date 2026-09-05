using System;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Streams;

namespace Orleans.Streaming.Kinesis
{
    /// <summary>
    /// Creates DynamoDB-backed stream queue checkpointers.
    /// </summary>
    public sealed class DynamoDBStreamQueueCheckpointerFactory : IStreamQueueCheckpointerFactory, IDisposable
    {
        private readonly string _providerName;
        private readonly string _serviceId;
        private readonly DynamoDBStreamQueueCheckpointerOptions _options;
        private readonly IAmazonDynamoDB _client;
        private readonly ILogger<DynamoDBStreamCheckpointStore> _logger;
        private readonly object _initializeLock = new();
        private readonly CancellationTokenSource _lifetimeCancellation = new();
        private Task? _initializeTask;
        private int _disposed;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public DynamoDBStreamQueueCheckpointerFactory(
            string providerName,
            DynamoDBStreamQueueCheckpointerOptions options,
            IOptions<ClusterOptions> clusterOptions,
            ILoggerFactory loggerFactory)
            : this(providerName, options, clusterOptions, loggerFactory, CreateClient(options))
        {
        }

        internal DynamoDBStreamQueueCheckpointerFactory(
            string providerName,
            DynamoDBStreamQueueCheckpointerOptions options,
            IOptions<ClusterOptions> clusterOptions,
            ILoggerFactory loggerFactory,
            IAmazonDynamoDB client)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(clusterOptions);
            ArgumentNullException.ThrowIfNull(loggerFactory);
            ArgumentNullException.ThrowIfNull(client);

            _providerName = providerName;
            _serviceId = clusterOptions.Value.ServiceId;
            ArgumentException.ThrowIfNullOrWhiteSpace(_serviceId);
            _options = options;
            _client = client;
            _logger = loggerFactory.CreateLogger<DynamoDBStreamCheckpointStore>();
        }

        /// <summary>
        /// Creates a factory from a service provider.
        /// </summary>
        public static IStreamQueueCheckpointerFactory CreateFactory(IServiceProvider services, string providerName)
        {
            var options = services.GetOptionsByName<DynamoDBStreamQueueCheckpointerOptions>(providerName);
            var clusterOptions = services.GetProviderClusterOptions(providerName);
            return ActivatorUtilities.CreateInstance<DynamoDBStreamQueueCheckpointerFactory>(
                services,
                providerName,
                options,
                clusterOptions);
        }

        /// <inheritdoc />
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        public async Task<IStreamQueueCheckpointer<string>> Create(string partition)
            => await Create(partition, CancellationToken.None);

        /// <inheritdoc />
        public async Task<IStreamQueueCheckpointer<string>> Create(
            string partition,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(partition);
            cancellationToken.ThrowIfCancellationRequested();

            await InitializeTable().WaitAsync(cancellationToken);
            var store = new DynamoDBStreamCheckpointStore(
                _client,
                _options.TableName,
                _serviceId,
                _providerName,
                partition);
            return await DynamoDBStreamQueueCheckpointer.Create(store, _options, cancellationToken);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _lifetimeCancellation.Cancel();
                _client.Dispose();
                _lifetimeCancellation.Dispose();
            }
        }

        internal static IAmazonDynamoDB CreateClient(DynamoDBStreamQueueCheckpointerOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (Uri.TryCreate(options.Service, UriKind.Absolute, out var serviceUri))
            {
                var config = new AmazonDynamoDBConfig
                {
                    AuthenticationRegion = GetAuthenticationRegion(options.Service),
                    ServiceURL = options.Service,
                };
                var endpointCredentials = CreateCredentials(options, useDummyCredentials: serviceUri.IsLoopback);
                return endpointCredentials is null
                    ? new AmazonDynamoDBClient(config)
                    : new AmazonDynamoDBClient(endpointCredentials, config);
            }

            var regionConfig = new AmazonDynamoDBConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(options.Service),
            };
            var credentials = CreateCredentials(options, useDummyCredentials: false);
            return credentials is null
                ? new AmazonDynamoDBClient(regionConfig)
                : new AmazonDynamoDBClient(credentials, regionConfig);
        }

        private static AWSCredentials? CreateCredentials(
            DynamoDBStreamQueueCheckpointerOptions options,
            bool useDummyCredentials)
        {
            if (!string.IsNullOrEmpty(options.AccessKey) && !string.IsNullOrEmpty(options.SecretKey))
            {
                return !string.IsNullOrEmpty(options.Token)
                    ? new SessionAWSCredentials(options.AccessKey, options.SecretKey, options.Token)
                    : new BasicAWSCredentials(options.AccessKey, options.SecretKey);
            }

            if (!string.IsNullOrEmpty(options.ProfileName))
            {
                var chain = new CredentialProfileStoreChain();
                if (chain.TryGetAWSCredentials(options.ProfileName, out var credentials))
                {
                    return credentials;
                }

                throw new InvalidOperationException(
                    $"AWS named profile '{options.ProfileName}' was configured, but its credentials could not be retrieved.");
            }

            return useDummyCredentials ? new BasicAWSCredentials("dummy", "dummy") : null;
        }

        private Task InitializeTable()
        {
            lock (_initializeLock)
            {
                if (_initializeTask is null || (_initializeTask.IsCompleted && !_initializeTask.IsCompletedSuccessfully))
                {
                    _initializeTask = DynamoDBStreamCheckpointStore.InitializeTable(
                        _client,
                        _options,
                        _logger,
                        _lifetimeCancellation.Token);
                }

                return _initializeTask;
            }
        }

        private static string GetAuthenticationRegion(string service)
        {
            var uri = new Uri(service, UriKind.Absolute);
            var hostSegments = uri.Host.Split('.');
            return hostSegments is [var dynamoDb, var region, ..]
                && dynamoDb.StartsWith("dynamodb", StringComparison.OrdinalIgnoreCase)
                && region.Contains('-', StringComparison.Ordinal)
                    ? region
                    : "us-east-1";
        }
    }
}
