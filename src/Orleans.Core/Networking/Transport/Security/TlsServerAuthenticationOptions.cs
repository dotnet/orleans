using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Orleans.Connections.Transport.Security;

/// <summary>
/// Selects the local certificate presented during a TLS server handshake.
/// </summary>
/// <param name="sender">The <see cref="SslStream"/> performing the handshake.</param>
/// <param name="hostName">The server name supplied by the client, when available.</param>
/// <returns>The certificate to present, or <see langword="null"/> when a certificate cannot be selected.</returns>
public delegate X509Certificate? ServerCertificateSelectionCallback(object sender, string? hostName);

/// <summary>
/// Configures authentication for an Orleans TLS server handshake.
/// </summary>
public class TlsServerAuthenticationOptions
{
    internal SslServerAuthenticationOptions Value { get; } = new SslServerAuthenticationOptions
    {
        ApplicationProtocols = new List<SslApplicationProtocol>
        {
            new SslApplicationProtocol("Orleans1")
        }
    };

    /// <summary>
    /// Gets or sets the certificate presented by the server.
    /// </summary>
    public X509Certificate? ServerCertificate
    {
        get => Value.ServerCertificate;
        set => Value.ServerCertificate = value;
    }

    /// <summary>
    /// Gets or sets the callback which selects the server certificate using the client's server name indication.
    /// </summary>
    public ServerCertificateSelectionCallback? ServerCertificateSelectionCallback
    {
        get => Value.ServerCertificateSelectionCallback is null ? null : new ServerCertificateSelectionCallback(Value.ServerCertificateSelectionCallback);
        set => Value.ServerCertificateSelectionCallback = value is null ? null : new System.Net.Security.ServerCertificateSelectionCallback(value!);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the server requests a client certificate.
    /// </summary>
    public bool ClientCertificateRequired
    {
        get => Value.ClientCertificateRequired;
        set => Value.ClientCertificateRequired = value;
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
    /// Gets the underlying <see cref="System.Net.Security.SslServerAuthenticationOptions"/> for additional handshake configuration.
    /// </summary>
    public object SslServerAuthenticationOptions => Value;
}
