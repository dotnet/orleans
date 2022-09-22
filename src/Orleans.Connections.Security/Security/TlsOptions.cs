using System;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Connections.Security
{
    public delegate bool RemoteCertificateValidator(X509Certificate2 certificate, X509Chain chain, SslPolicyErrors policyErrors);

    /// <summary>
    /// Settings for how TLS connections are handled.
    /// </summary>
    public class TlsOptions
    {
        private TimeSpan _handshakeTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// <para>
        /// Specifies the local certificate used to authenticate TLS connections. This is ignored on server if LocalCertificateSelector is set.
        /// </para>
        /// <para>
        /// To omit client authentication set to <c>null</c> on client and set <see cref="RemoteCertificateMode"/> to <see cref="Orleans.Connections.Security.RemoteCertificateMode.AllowCertificate"/> or <see cref="Orleans.Connections.Security.RemoteCertificateMode.NoCertificate"/> on server.
        /// </para>
        /// <para>
        /// If the certificate has an Extended Key Usage extension, the usages must include Server Authentication (OID 1.3.6.1.5.5.7.3.1) for server and Client Authentication (OID 1.3.6.1.5.5.7.3.2) for client.
        /// </para>
        /// </summary>
        public X509Certificate2 LocalCertificate { get; set; }

        /// <summary>
        /// <para>
        /// A callback that will be invoked to dynamically select a local server certificate. This is higher priority than LocalCertificate.
        /// If SNI is not available then the name parameter will be null.
        /// </para>
        /// <para>
        /// If the certificate has an Extended Key Usage extension, the usages must include Server Authentication (OID 1.3.6.1.5.5.7.3.1).
        /// </para>
        /// </summary>
        public Func<ConnectionContext, string, X509Certificate2> LocalServerCertificateSelector { get; set; }

        /// <summary>
        /// <para>
        /// A callback that will be invoked to dynamically select a local client certificate. This is higher priority than LocalCertificate.
        /// </para>
        /// <para>
        /// If the certificate has an Extended Key Usage extension, the usages must include Client Authentication (OID 1.3.6.1.5.5.7.3.2).
        /// </para>
        /// </summary>
        public Func<object, string, X509CertificateCollection, X509Certificate, string[], X509Certificate2> LocalClientCertificateSelector { get; set; }

        /// <summary>
        /// Specifies the remote endpoint certificate requirements for a TLS connection. Defaults to <see cref="Orleans.Connections.Security.RemoteCertificateMode.RequireCertificate"/>.
        /// </summary>
        public RemoteCertificateMode RemoteCertificateMode { get; set; } = RemoteCertificateMode.RequireCertificate;

        /// <summary>
        /// Specifies the client authentication certificate requirements for a TLS connection to Silo. Defaults to <see cref="Orleans.Connections.Security.RemoteCertificateMode.AllowCertificate"/>.
        /// </summary>
        public RemoteCertificateMode ClientCertificateMode { get; set; } = RemoteCertificateMode.AllowCertificate;

        /// <summary>
        /// Specifies a callback for additional remote certificate validation that will be invoked during authentication. This will be ignored
        /// if <see cref="AllowAnyRemoteCertificate"/> is called after this callback is set.
        /// </summary>
        public RemoteCertificateValidator RemoteCertificateValidation { get; set; }

        /// <summary>
        /// Specifies allowable SSL protocols. Defaults to <see cref="System.Security.Authentication.SslProtocols.Tls13" /> and <see cref="System.Security.Authentication.SslProtocols.Tls12"/>.
        /// </summary>
        public SslProtocols SslProtocols { get; set; } = SslProtocols.Tls13 | SslProtocols.Tls12;

        /// <summary>
        /// Specifies whether the certificate revocation list is checked during authentication.
        /// </summary>
        public bool CheckCertificateRevocation { get; set; }

        /// <summary>
        /// Overrides the current <see cref="RemoteCertificateValidation"/> callback and allows any client certificate.
        /// </summary>
        public void AllowAnyRemoteCertificate()
        {
            RemoteCertificateValidation = (_, __, ___) => true;
        }

        /// <summary>
        /// Provides direct configuration of the <see cref="TlsServerAuthenticationOptions"/> on a per-connection basis.
        /// This is called after all of the other settings have already been applied.
        /// </summary>
        public Action<ConnectionContext, TlsServerAuthenticationOptions> OnAuthenticateAsServer { get; set; }

        /// <summary>
        /// Provides direct configuration of the <see cref="TlsClientAuthenticationOptions"/> on a per-connection basis.
        /// This is called after all of the other settings have already been applied.
        /// </summary>
        public Action<ConnectionContext, TlsClientAuthenticationOptions> OnAuthenticateAsClient { get; set; }

        /// <summary>
        /// Specifies the maximum amount of time allowed for the TLS/SSL handshake. This must be positive and finite.
        /// </summary>
        public TimeSpan HandshakeTimeout
        {
            get => _handshakeTimeout;
            set
            {
                if (value <= TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), nameof(HandshakeTimeout) + " must be positive");
                }

                _handshakeTimeout = value != Timeout.InfiniteTimeSpan ? value : TimeSpan.MaxValue;
            }
        }
    }
}
