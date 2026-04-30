#nullable enable

using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Connections.Transport.Security;

/// <summary>
/// Message transport encrypts and decrypts all data using TLS, authenticating with the remote endpoint as a server.
/// </summary>
public class ServerTlsMessageTransport : TlsMessageTransport
{
    private readonly X509Certificate2? _certificate;
    private readonly Func<MessageTransport, string, X509Certificate2>? _certificateSelector;

    public ServerTlsMessageTransport(MessageTransport transport, TlsOptions options, ILogger logger) : base(transport, options, logger)
    {
        // Capture the certificate now so it can't be switched after validation
        _certificate = options.LocalCertificate;
        _certificateSelector = options.LocalServerCertificateSelector;

        if (_certificate is null && _certificateSelector is null)
        {
            throw new InvalidOperationException($"Either {nameof(TlsOptions)}.{nameof(TlsOptions.LocalCertificate)} or {nameof(TlsOptions)}.{nameof(TlsOptions.LocalServerCertificateSelector)} must be set to a non-null value.");
        }

        // If a selector is provided then ignore the cert, it may be a default cert.
        if (_certificateSelector is not null)
        {
            // SslStream doesn't allow both.
            _certificate = null;
        }
        else if (_certificate is not null)
        {
            EnsureCertificateIsAllowedForServerAuth(_certificate);
        }
    }

    protected override async Task AuthenticateAsyncCore(MessageTransport transport, bool certificateRequired, CancellationToken cancellationToken)
    {
        // Adapt to the SslStream signature
        ServerCertificateSelectionCallback? selector = null;
        if (_certificateSelector != null)
        {
            selector = (sender, name) =>
            {
                var cert = _certificateSelector(transport, name);
                if (cert != null)
                {
                    EnsureCertificateIsAllowedForServerAuth(cert);
                }

                return cert;
            };
        }

        var sslOptions = new TlsServerAuthenticationOptions
        {
            ServerCertificate = _certificate,
            ServerCertificateSelectionCallback = selector,
            ClientCertificateRequired = certificateRequired,
            EnabledSslProtocols = Options.SslProtocols,
            CertificateRevocationCheckMode = Options.CheckCertificateRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
        };

        Options.OnAuthenticateAsServer?.Invoke(transport, sslOptions);

        await Stream.AuthenticateAsServerAsync(sslOptions.Value, cancellationToken);
    }

    protected static void EnsureCertificateIsAllowedForServerAuth(X509Certificate2 certificate)
    {
        if (!CertificateLoader.IsCertificateAllowedForServerAuth(certificate))
        {
            throw new InvalidOperationException($"Invalid server certificate for server authentication: {certificate.Thumbprint}");
        }
    }
}
