#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Orleans.Connections.Transport.Security;

/// <summary>
/// Message transport listener which configures transports for TLS.
/// </summary>
public class TlsMessageTransportListener(
    MessageTransportListener innerListener,
    IOptionsMonitor<TlsOptions> tlsOptions,
    ILoggerFactory loggerFactory) : MessageTransportListener
{
    private readonly IOptionsMonitor<TlsOptions> _tlsOptions = tlsOptions;
    private readonly MessageTransportListener _innerListener = innerListener;
    private readonly ILogger _logger = loggerFactory.CreateLogger<ServerTlsMessageTransport>();

    /// <inheritdoc/>
    public override IFeatureCollection Features => _innerListener.Features;

    /// <inheritdoc/>
    public override bool IsValid => _innerListener.IsValid;

    /// <inheritdoc/>
    public override string ListenerName => _innerListener.ListenerName;

    /// <inheritdoc/>
    public override async ValueTask<MessageTransport?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        var innerTransport = await _innerListener.AcceptAsync(cancellationToken).ConfigureAwait(false);
        if (innerTransport is null)
        {
            return null;
        }

        var transport = new ServerTlsMessageTransport(innerTransport, _tlsOptions.Get(ListenerName), _logger);
        transport.Start();
        return transport;
    }

    /// <inheritdoc/>
    public override async ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        await _innerListener.BindAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public override ValueTask UnbindAsync(CancellationToken cancellationToken = default) => _innerListener.UnbindAsync(cancellationToken);

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await _innerListener.DisposeAsync();
        await base.DisposeAsync();
    }
}
