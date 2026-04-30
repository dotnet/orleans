#nullable enable

using System;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Connections.Transport.Streams;

namespace Orleans.Connections.Transport.Security;

/// <summary>
/// <see cref="MessageTransport"/> which encrypts and decrypts all data using TLS.
/// </summary>
public abstract class TlsMessageTransport : StreamMessageTransport
{
    private readonly MessageTransport _innerTransport;
    private readonly TlsOptions _options;
    private readonly ILogger _logger;
    private readonly MessageTransportStream _networkTransportStream;
    private readonly SslStream _sslStream;

    /// <summary>
    /// Initializes a new <see cref="TlsMessageTransport"/> instance.
    /// </summary>
    /// <param name="transport"></param>
    /// <param name="options"></param>
    /// <param name="logger"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public TlsMessageTransport(MessageTransport transport, TlsOptions options, ILogger logger) : base(logger)
    {
        _innerTransport = transport;

        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _networkTransportStream = new MessageTransportStream(_innerTransport, _options.MemoryPool);
        _sslStream = new SslStream(
                _networkTransportStream,
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (sender, certificate, chain, sslPolicyErrors) =>
                {
                    if (certificate == null)
                    {
                        return _options.RemoteCertificateMode != RemoteCertificateMode.RequireCertificate;
                    }

                    if (_options.RemoteCertificateValidation == null)
                    {
                        if (sslPolicyErrors != SslPolicyErrors.None)
                        {
                            return false;
                        }
                    }

                    var certificate2 = ConvertToX509Certificate2(certificate);
                    if (certificate2 == null)
                    {
                        return false;
                    }

                    if (_options.RemoteCertificateValidation != null)
                    {
                        if (!_options.RemoteCertificateValidation(certificate2, chain, sslPolicyErrors))
                        {
                            return false;
                        }
                    }

                    return true;
                });
    }

    /// <summary>
    /// Gets the TLS options.
    /// </summary>
    protected TlsOptions Options => _options;

    /// <summary>
    /// Gets the underlying <see cref="SslStream"/>.
    /// </summary>
    protected override SslStream Stream => _sslStream;

    /// <summary>
    /// Gets the underlying <see cref="MessageTransport"/>.
    /// </summary>
    protected MessageTransport InnerTransport => _innerTransport;

    /// <inheritdoc/>
    public override async ValueTask CloseAsync(Exception? closeException, CancellationToken cancellationToken = default)
    {
        // Close outer stream loops FIRST (StreamMessageTransport)
        await base.CloseAsync(closeException, cancellationToken).ConfigureAwait(false);

        // Then close inner transport (socket)
        await _innerTransport.CloseAsync(closeException, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override async Task RunAsyncCore()
    {
        try
        {
            await AuthenticateAsync().ConfigureAwait(false);
            await base.RunAsyncCore().ConfigureAwait(false);
        }
        finally
        {
            await CloseAsync(closeException: null, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task AuthenticateAsync()
    {
        bool certificateRequired;

        if (_options.RemoteCertificateMode == RemoteCertificateMode.NoCertificate)
        {
            certificateRequired = false;
        }
        else
        {
            certificateRequired = true;
        }

        using (var cancellationTokenSource = new CancellationTokenSource(_options.HandshakeTimeout))
        {
            try
            {
                await AuthenticateAsyncCore(_innerTransport, certificateRequired, cancellationTokenSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                _logger?.LogWarning(2, ex, "Authentication timed out");
                await _sslStream.DisposeAsync().ConfigureAwait(false);
                await _innerTransport.CloseAsync(ex).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(1, ex, "Authentication failed");
                await _sslStream.DisposeAsync().ConfigureAwait(false);
                await _innerTransport.CloseAsync(ex).ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <inheritdoc/>
    protected abstract Task AuthenticateAsyncCore(MessageTransport transport, bool certificateRequired, CancellationToken cancellationToken);

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await CloseAsync(null, CancellationToken.None).ConfigureAwait(false);

        // SslStream disposes _networkTransportStream since leaveInnerStreamOpen: false
        await _sslStream.DisposeAsync().ConfigureAwait(false);

        // Dispose inner transport last
        await _innerTransport.DisposeAsync().ConfigureAwait(false);

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static X509Certificate2? ConvertToX509Certificate2(X509Certificate? certificate)
    {
        return certificate switch
        {
            null => null,
            X509Certificate2 certificate2 => certificate2,
            _ => new X509Certificate2(certificate)
        };
    }

    public override string ToString() => $"Tls({_innerTransport})";
}
