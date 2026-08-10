using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Streams;
using RabbitMQ.Stream.Client;

namespace Orleans.Streaming.RabbitMQ.RabbitMQ;

internal class RabbitMQStreamSystemProvider : IAsyncDisposable
{
    private readonly object _producerLock = new();
    private readonly object _consumerLock = new();
    private readonly ILogger<RabbitMQStreamSystemProvider> _logger;
    private readonly RabbitMQClientOptions _rabbitMqClientOptions;
    private Task<StreamSystem> _createProducerStreamTask;
    private StreamSystem _consumerStreamSystem;
    private Task<StreamSystem> _createConsumerStreamTask;
    private StreamSystem _producerStreamSystem;

    public RabbitMQStreamSystemProvider(RabbitMQClientOptions options,
        ILogger<RabbitMQStreamSystemProvider> logger)
    {
        _logger = logger;
        _rabbitMqClientOptions = options;
    }

    public async ValueTask<StreamSystem> GetConsumerStream()
    {
        if (_consumerStreamSystem is not null)
        {
            return _consumerStreamSystem;
        }

        lock (_consumerLock)
        {
            _createConsumerStreamTask ??= CreateConsumerStreamSystem();
        }

        try
        {
            return await _createConsumerStreamTask.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to create consumer stream provider");
            throw;
        }
    }

    public async ValueTask<StreamSystem> GetProducerStream()
    {
        if (_producerStreamSystem is not null)
        {
            return _producerStreamSystem;
        }

        lock (_producerLock)
        {
            _createProducerStreamTask ??= CreateProducerStreamSystem();
        }

        try
        {
            return await _createProducerStreamTask.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to create producer stream provider");
            throw;
        }
    }

    private async Task<StreamSystem> CreateProducerStreamSystem()
    {
        _producerStreamSystem = await CreateStreamSystem().ConfigureAwait(false);

        return _producerStreamSystem;
    }

    private async Task<StreamSystem> CreateConsumerStreamSystem()
    {
        _consumerStreamSystem = await CreateStreamSystem().ConfigureAwait(false);
        return _consumerStreamSystem;
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
                LogFailedToConnectToRabbitStream(
                    exception,
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

    private void LogFailedToConnectToRabbitStream(
        Exception exception,
        int attempt,
        int maxAttempts,
        TimeSpan delay) =>
        _logger.LogError(exception,
            "RabbitMQ connection attempt {Attempt} of {MaxAttempts} failed, retrying in {Delay}",
            attempt,
            maxAttempts,
            delay);

    public async ValueTask DisposeAsync()

    {
        _createConsumerStreamTask?.Dispose();
        _createProducerStreamTask?.Dispose();

        if (_producerStreamSystem is not null)
        {
            await _producerStreamSystem.Close().ConfigureAwait(false);
        }

        if (_consumerStreamSystem is not null)
        {
            await _consumerStreamSystem.Close().ConfigureAwait(false);
        }
    }
}