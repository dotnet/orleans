using Microsoft.Extensions.Options;

namespace Orleans.Streaming.RabbitMQ.RabbitMQ;

internal sealed class RabbitMQClientOptionsFormatterResolver : IOptionFormatterResolver<RabbitMQClientOptions>
{
    private readonly IOptionsMonitor<RabbitMQClientOptions> _options;

    public RabbitMQClientOptionsFormatterResolver(IOptionsMonitor<RabbitMQClientOptions> options)
    {
        _options = options;
    }

    public IOptionFormatter<RabbitMQClientOptions> Resolve(string name) =>
        new RabbitMQClientOptionsFormatter(name, _options.Get(name));
}

internal sealed class RabbitMQClientOptionsFormatter : IOptionFormatter<RabbitMQClientOptions>
{
    private readonly RabbitMQClientOptions _options;

    public RabbitMQClientOptionsFormatter(string name, RabbitMQClientOptions options)
    {
        Name = OptionFormattingUtilities.Name<RabbitMQClientOptions>(name);
        _options = options;
    }

    public string Name { get; }

    public IEnumerable<string> Format()
    {
        yield return OptionFormattingUtilities.Format(
            nameof(RabbitMQClientOptions.IntervalToUpdateOffset),
            _options.IntervalToUpdateOffset);
        yield return OptionFormattingUtilities.Format(
            $"{nameof(RabbitMQClientOptions.ConnectionRetry)}.{nameof(RabbitMQConnectionRetryOptions.MaxAttempts)}",
            _options.ConnectionRetry.MaxAttempts);
        yield return OptionFormattingUtilities.Format(
            $"{nameof(RabbitMQClientOptions.ConnectionRetry)}.{nameof(RabbitMQConnectionRetryOptions.Delay)}",
            _options.ConnectionRetry.Delay);
        yield return OptionFormattingUtilities.Format(
            $"{nameof(RabbitMQClientOptions.StreamSystemConfig)}.VirtualHost",
            _options.StreamSystemConfig.VirtualHost);
        yield return OptionFormattingUtilities.Format(
            $"{nameof(RabbitMQClientOptions.StreamSystemConfig)}.Heartbeat",
            _options.StreamSystemConfig.Heartbeat);
        yield return OptionFormattingUtilities.Format(
            $"{nameof(RabbitMQClientOptions.StreamSystemConfig)}.ClientProvidedName",
            _options.StreamSystemConfig.ClientProvidedName);
        yield return OptionFormattingUtilities.Format(
            $"{nameof(RabbitMQClientOptions.StreamSystemConfig)}.Ssl.Enabled",
            _options.StreamSystemConfig.Ssl.Enabled);

        for (var i = 0; i < _options.StreamSystemConfig.Endpoints.Count; i++)
        {
            yield return OptionFormattingUtilities.Format(
                $"{nameof(RabbitMQClientOptions.StreamSystemConfig)}.Endpoints.{i}",
                _options.StreamSystemConfig.Endpoints[i]);
        }

        if (_options.QueueNames is not null)
        {
            for (var i = 0; i < _options.QueueNames.Count; i++)
            {
                yield return OptionFormattingUtilities.Format($"{nameof(RabbitMQClientOptions.QueueNames)}.{i}", _options.QueueNames[i]);
            }
        }
    }
}
