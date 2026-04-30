#nullable enable

using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Orleans.Connections.Transport.Security;

/// <summary>
/// Message transport factory which configures transports for TLS.
/// </summary>
public class TlsMessageTransportConnector(
    MessageTransportConnector innerTransportFactory,
    IOptionsMonitor<TlsOptions> tlsOptions,
    ILoggerFactory loggerFactory) : MessageTransportConnector
{
    private readonly MessageTransportConnector _innerConnector = innerTransportFactory;
    private readonly ILogger<ClientTlsMessageTransport> _logger = loggerFactory.CreateLogger<ClientTlsMessageTransport>();
    private readonly IOptionsMonitor<TlsOptions> _tlsOptions = tlsOptions;

    /// <inheritdoc/>
    public override IFeatureCollection Features => _innerConnector.Features;

    /// <inheritdoc/>
    public override bool IsValid => _innerConnector.IsValid;

    /// <inheritdoc/>
    public override async ValueTask<MessageTransport> CreateAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        var innerTransport = await _innerConnector.CreateAsync(endPoint, cancellationToken);
        var tlsOptions = _tlsOptions.CurrentValue;
        var transport = new ClientTlsMessageTransport(innerTransport, tlsOptions, _logger);
        transport.Start();
        return transport;
    }

    /// <inheritdoc/>
    public override ValueTask DisposeAsync() => _innerConnector.DisposeAsync();
}
