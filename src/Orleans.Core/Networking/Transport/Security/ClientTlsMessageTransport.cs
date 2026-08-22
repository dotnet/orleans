#nullable enable
using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Connections.Transport.Security;

/// <summary>
/// Message transport encrypts and decrypts all data using TLS, authenticating with the remote endpoint as a client.
/// </summary>
public class ClientTlsMessageTransport : TlsMessageTransport
{
    private readonly X509Certificate2? _certificate;
    private readonly Func<object, string, X509CertificateCollection, X509Certificate, string[], X509Certificate2>? _certificateSelector;

    public ClientTlsMessageTransport(MessageTransport transport, TlsOptions options, ILogger logger) : base(transport, options, logger)
    {
        // Capture the certificate now so it can't be switched after validation
        _certificate = options.LocalCertificate;
        _certificateSelector = options.LocalClientCertificateSelector;

        // If a selector is provided then ignore the cert, it may be a default cert.
        if (_certificateSelector is not null)
        {
            // SslStream doesn't allow both.
            _certificate = null;
        }
        else if (_certificate is not null)
        {
            _certificate = ValidateCertificate(_certificate, options.ClientCertificateMode);
        }

        if (_certificate is null && _certificateSelector is null && options.ClientCertificateMode == RemoteCertificateMode.RequireCertificate)
        {
            throw new InvalidOperationException($"Either {nameof(TlsOptions)}.{nameof(TlsOptions.LocalCertificate)} or {nameof(TlsOptions)}.{nameof(TlsOptions.LocalClientCertificateSelector)} must be set to a non-null"
                + $"value because {nameof(TlsOptions)}.{nameof(TlsOptions.ClientCertificateMode)} is set to {nameof(RemoteCertificateMode)}.{nameof(RemoteCertificateMode.RequireCertificate)}.");
        }
    }

    protected override async Task AuthenticateAsyncCore(MessageTransport transport, bool certificateRequired, CancellationToken cancellationToken)
    {
        ClientCertificateSelectionCallback? selector = null;
        if (_certificateSelector != null)
        {
            selector = (sender, targetHost, localCertificates, remoteCertificate, acceptableIssuers) =>
            {
                var cert = _certificateSelector(sender, targetHost, localCertificates, remoteCertificate, acceptableIssuers);
                if (cert != null)
                {
                    cert = ValidateCertificate(cert, Options.ClientCertificateMode);
                }

                return cert;
            };
        }

        var sslOptions = new TlsClientAuthenticationOptions
        {
            ClientCertificates = _certificate == null || _certificateSelector != null ? null : new X509CertificateCollection { _certificate },
            LocalCertificateSelectionCallback = selector,
            EnabledSslProtocols = Options.SslProtocols,
        };

        Options.OnAuthenticateAsClient?.Invoke(transport, sslOptions);

        await Stream.AuthenticateAsClientAsync(sslOptions.Value, cancellationToken);
    }

    private static X509Certificate2? ValidateCertificate(X509Certificate2 certificate, RemoteCertificateMode mode)
    {
        switch (mode)
        {
            case RemoteCertificateMode.NoCertificate:
                return null;
            case RemoteCertificateMode.AllowCertificate:
                // If certificate exists but can not be used for client authentication.
                if (certificate != null && CertificateLoader.IsCertificateAllowedForClientAuth(certificate))
                {
                    return certificate;
                }

                return null;
            case RemoteCertificateMode.RequireCertificate:
                EnsureCertificateIsAllowedForClientAuth(certificate);
                return certificate;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    protected static void EnsureCertificateIsAllowedForClientAuth(X509Certificate2 certificate)
    {
        if (certificate is null)
        {
            throw new InvalidOperationException("No certificate provided for client authentication.");
        }

        if (!CertificateLoader.IsCertificateAllowedForClientAuth(certificate))
        {
            throw new InvalidOperationException($"Invalid client certificate for client authentication: {certificate.Thumbprint}");
        }
    }
}
