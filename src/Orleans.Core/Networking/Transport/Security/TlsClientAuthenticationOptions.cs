using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Orleans.Connections.Transport.Security;

/// <summary>
/// Selects the local certificate presented during a TLS client handshake.
/// </summary>
/// <param name="sender">The <see cref="SslStream"/> performing the handshake.</param>
/// <param name="targetHost">The name of the remote host.</param>
/// <param name="localCertificates">The available local certificates.</param>
/// <param name="remoteCertificate">The remote certificate, when available.</param>
/// <param name="acceptableIssuers">The certificate issuers accepted by the remote host.</param>
/// <returns>The certificate to present, or <see langword="null"/> to proceed without a client certificate.</returns>
public delegate X509Certificate? ClientCertificateSelectionCallback(object sender, string targetHost, X509CertificateCollection localCertificates, X509Certificate? remoteCertificate, string[] acceptableIssuers);

/// <summary>
/// Configures authentication for an Orleans TLS client handshake.
/// </summary>
public class TlsClientAuthenticationOptions
{
    internal SslClientAuthenticationOptions Value { get; } = new SslClientAuthenticationOptions
    {
        ApplicationProtocols = new List<SslApplicationProtocol>
        {
            new SslApplicationProtocol("Orleans1")
        }
    };

    /// <summary>
    /// Gets or sets the callback which selects the local client certificate.
    /// </summary>
    public ClientCertificateSelectionCallback? LocalCertificateSelectionCallback
    {
        get => Value.LocalCertificateSelectionCallback is null ? null : new ClientCertificateSelectionCallback(Value.LocalCertificateSelectionCallback);
        set
        {
#if NET10_0_OR_GREATER
            Value.LocalCertificateSelectionCallback = value is null ? null : new LocalCertificateSelectionCallback(value);
#else
            Value.LocalCertificateSelectionCallback = value is null
                ? null
                : (sender, targetHost, localCertificates, remoteCertificate, acceptableIssuers) =>
                    value(sender, targetHost, localCertificates, remoteCertificate, acceptableIssuers)!;
#endif
        }
    }

    /// <summary>
    /// Gets or sets the certificates available for client authentication.
    /// </summary>
    public X509CertificateCollection? ClientCertificates
    {
        get => Value.ClientCertificates;
        set => Value.ClientCertificates = value;
    }

    /// <summary>
    /// Gets or sets the TLS protocol versions allowed for the handshake.
    /// </summary>
    public SslProtocols EnabledSslProtocols
    {
        get => Value.EnabledSslProtocols;
        set => Value.EnabledSslProtocols = value;
    }

    /// <summary>
    /// Gets or sets how the remote certificate's revocation status is checked.
    /// </summary>
    public X509RevocationMode CertificateRevocationCheckMode
    {
        get => Value.CertificateRevocationCheckMode;
        set => Value.CertificateRevocationCheckMode = value;
    }

    /// <summary>
    /// Gets or sets the remote host name used for certificate validation and TLS server name indication.
    /// </summary>
    public string? TargetHost
    {
        get => Value.TargetHost;
        set => Value.TargetHost = value;
    }

    /// <summary>
    /// Gets the underlying <see cref="System.Net.Security.SslClientAuthenticationOptions"/> for additional handshake configuration.
    /// </summary>
    public object SslClientAuthenticationOptions => Value;
}
