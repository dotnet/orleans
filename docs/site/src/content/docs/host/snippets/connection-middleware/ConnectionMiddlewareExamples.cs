using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Connections.Transport;

namespace ConnectionMiddlewareSnippets;

internal static class ConnectionMiddlewareExamples
{
    // <RegisterMiddleware>
    public static void RegisterTransportMiddleware(IServiceCollection services)
    {
        services.AddSingleton<IMessageTransportConnectorMiddleware, LoggingConnectorMiddleware>();
        services.AddSingleton<IMessageTransportListenerMiddleware, LoggingListenerMiddleware>();
    }
    // </RegisterMiddleware>
}

internal sealed class LoggingConnectorMiddleware(ILoggerFactory loggerFactory)
    : IMessageTransportConnectorMiddleware
{
    public MessageTransportConnector Apply(MessageTransportConnector transport) =>
        new LoggingConnector(transport, loggerFactory.CreateLogger<LoggingConnector>());
}

internal sealed class LoggingListenerMiddleware(ILoggerFactory loggerFactory)
    : IMessageTransportListenerMiddleware
{
    public MessageTransportListener Apply(MessageTransportListener listener) =>
        new LoggingListener(listener, loggerFactory.CreateLogger<LoggingListener>());
}

// <ConnectorDecorator>
internal sealed class LoggingConnector(
    MessageTransportConnector inner,
    ILogger<LoggingConnector> logger) : MessageTransportConnector
{
    public override IFeatureCollection Features => inner.Features;
    public override bool IsValid => inner.IsValid;

    public override async ValueTask<MessageTransport> CreateAsync(
        EndPoint endpoint,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Connecting Orleans transport to {Endpoint}", endpoint);
        return await inner.CreateAsync(endpoint, cancellationToken);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
// </ConnectorDecorator>

// <ListenerDecorator>
internal sealed class LoggingListener(
    MessageTransportListener inner,
    ILogger<LoggingListener> logger) : MessageTransportListener
{
    public override bool IsValid => inner.IsValid;
    public override string ListenerName => inner.ListenerName;
    public override IFeatureCollection Features => inner.Features;

    public override ValueTask BindAsync(CancellationToken cancellationToken = default) =>
        inner.BindAsync(cancellationToken);

    public override async ValueTask<MessageTransport?> AcceptAsync(
        CancellationToken cancellationToken = default)
    {
        var transport = await inner.AcceptAsync(cancellationToken);
        if (transport is not null)
        {
            logger.LogInformation(
                "Accepted Orleans transport on listener {ListenerName}",
                ListenerName);
        }

        return transport;
    }

    public override ValueTask UnbindAsync(CancellationToken cancellationToken = default) =>
        inner.UnbindAsync(cancellationToken);

    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
// </ListenerDecorator>
