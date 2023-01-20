using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Orleans.Streams;
using Polly;
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
        var (exceptionsAllowedBeforeBreaking, durationOfBreak) = _rabbitMqClientOptions.CircuitBreakConnectionConfig;

        var circuitBreakerPolicy = Policy.Handle<Exception>().CircuitBreakerAsync(exceptionsAllowedBeforeBreaking,
            durationOfBreak, LogFailedToConnectToRabbitStream, LogCreateStreamRetry);
        var streamSystem = await circuitBreakerPolicy.ExecuteAsync(
                () => StreamSystem.Create(_rabbitMqClientOptions.StreamSystemConfig)).ConfigureAwait(false);
        _logger.LogInformation("RabbitMQ stream system created");


        return streamSystem;
    }

    private void LogFailedToConnectToRabbitStream(Exception exception, TimeSpan durationOfBreak) =>
        _logger.LogError(exception,
            "Failed to connect to rabbit, retrying in {DurationOfBreak} seconds", durationOfBreak);

    private void LogCreateStreamRetry()
        => _logger.LogInformation("Retrying creation of RabbitMQ Stream connection");

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