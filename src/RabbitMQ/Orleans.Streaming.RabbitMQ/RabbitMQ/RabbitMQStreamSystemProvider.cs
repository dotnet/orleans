using Microsoft.Extensions.Logging;
using Orleans.Internal;
using RabbitMQ.Stream.Client;

namespace Orleans.Streaming.RabbitMQ.RabbitMQ;

internal sealed class RabbitMQStreamSystemProvider : IAsyncDisposable
{
    private readonly object _producerLock = new();
    private readonly object _consumerLock = new();
    private readonly ILogger<RabbitMQStreamSystemProvider> _logger;
    private readonly RabbitMQClientOptions _rabbitMqClientOptions;
    private Task<StreamSystem> _createProducerStreamTask;
    private StreamSystem _consumerStreamSystem;
    private Task<StreamSystem> _createConsumerStreamTask;
    private StreamSystem _producerStreamSystem;
    private volatile bool _disposed;

    public RabbitMQStreamSystemProvider(
        RabbitMQClientOptions options,
        ILogger<RabbitMQStreamSystemProvider> logger)
    {
        _logger = logger;
        _rabbitMqClientOptions = options;
    }

    public ValueTask<StreamSystem> GetConsumerStream() =>
        GetStreamSystem(
            _consumerLock,
            () => _consumerStreamSystem,
            value => _consumerStreamSystem = value,
            () => _createConsumerStreamTask,
            value => _createConsumerStreamTask = value);

    public ValueTask<StreamSystem> GetProducerStream() =>
        GetStreamSystem(
            _producerLock,
            () => _producerStreamSystem,
            value => _producerStreamSystem = value,
            () => _createProducerStreamTask,
            value => _createProducerStreamTask = value);

    private async ValueTask<StreamSystem> GetStreamSystem(
        object sync,
        Func<StreamSystem> getStreamSystem,
        Action<StreamSystem> setStreamSystem,
        Func<Task<StreamSystem>> getCreationTask,
        Action<Task<StreamSystem>> setCreationTask)
    {
        Task<StreamSystem> creationTask;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = getStreamSystem();
            if (current is { IsClosed: false })
            {
                return current;
            }

            if (current is not null)
            {
                setStreamSystem(null);
                setCreationTask(null);
            }

            creationTask = getCreationTask();
            if (creationTask is null)
            {
                setCreationTask(creationTask = CreateStreamSystem());
            }
        }

        try
        {
            var streamSystem = await creationTask.ConfigureAwait(false);
            var accepted = false;
            lock (sync)
            {
                if (!_disposed && ReferenceEquals(getCreationTask(), creationTask))
                {
                    setStreamSystem(streamSystem);
                    accepted = true;
                }
            }

            if (!accepted)
            {
                await streamSystem.Close().ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(RabbitMQStreamSystemProvider));
            }

            return streamSystem;
        }
        catch
        {
            lock (sync)
            {
                if (ReferenceEquals(getCreationTask(), creationTask))
                {
                    setCreationTask(null);
                }
            }

            throw;
        }
    }

    private async Task<StreamSystem> CreateStreamSystem()
    {
        _logger.LogInformation("Creating RabbitMQ stream system");
        var retryOptions = _rabbitMqClientOptions.ConnectionRetry;
        ArgumentOutOfRangeException.ThrowIfLessThan(retryOptions.MaxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryOptions.Delay, TimeSpan.Zero);

        var streamSystem = await AsyncExecutorWithRetries.ExecuteWithRetries(
            _ => StreamSystem.Create(_rabbitMqClientOptions.StreamSystemConfig),
            retryOptions.MaxAttempts,
            (exception, attempt) =>
            {
                _logger.LogError(
                    exception,
                    "RabbitMQ connection attempt {Attempt} of {MaxAttempts} failed, retrying in {Delay}",
                    attempt + 1,
                    retryOptions.MaxAttempts,
                    retryOptions.Delay);
                return true;
            },
            Timeout.InfiniteTimeSpan,
            new FixedBackoff(retryOptions.Delay)).ConfigureAwait(false);
        _logger.LogInformation("RabbitMQ stream system created");
        return streamSystem;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        StreamSystem producer;
        StreamSystem consumer;
        lock (_producerLock)
        {
            producer = _producerStreamSystem;
            _producerStreamSystem = null;
            _createProducerStreamTask = null;
        }

        lock (_consumerLock)
        {
            consumer = _consumerStreamSystem;
            _consumerStreamSystem = null;
            _createConsumerStreamTask = null;
        }

        if (producer is not null)
        {
            await producer.Close().ConfigureAwait(false);
        }

        if (consumer is not null && !ReferenceEquals(consumer, producer))
        {
            await consumer.Close().ConfigureAwait(false);
        }
    }
}
