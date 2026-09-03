using System;
using System.Net.Security;
using System.Security.Authentication;

namespace Orleans.Connections.Security
{
    /// <summary>
    /// Provides information about the negotiated TLS handshake.
    /// </summary>
    public interface ITlsHandshakeFeature
    {
        /// <summary>
        /// Gets the negotiated TLS protocol version.
        /// </summary>
        SslProtocols Protocol { get; }

        /// <summary>
        /// Gets the <see cref="TlsCipherSuite"/>.
        /// </summary>
        TlsCipherSuite? NegotiatedCipherSuite => null;

        /// <summary>
        /// Gets the host name from the "server_name" (SNI) extension of the client hello if present.
        /// </summary>
        string HostName => string.Empty;

        /// <summary>
        /// Gets the negotiated bulk encryption algorithm.
        /// </summary>
#if NET10_0_OR_GREATER
        [Obsolete("KeyExchangeAlgorithm, KeyExchangeStrength, CipherAlgorithm, CipherStrength, HashAlgorithm and HashStrength properties are obsolete. Use NegotiatedCipherSuite instead.", DiagnosticId = "SYSLIB0058", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
#endif
        CipherAlgorithmType CipherAlgorithm { get; }

        /// <summary>
        /// Gets the strength, in bits, of the negotiated bulk encryption algorithm.
        /// </summary>
#if NET10_0_OR_GREATER
        [Obsolete("KeyExchangeAlgorithm, KeyExchangeStrength, CipherAlgorithm, CipherStrength, HashAlgorithm and HashStrength properties are obsolete. Use NegotiatedCipherSuite instead.", DiagnosticId = "SYSLIB0058", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
#endif
        int CipherStrength { get; }

        /// <summary>
        /// Gets the negotiated message authentication algorithm.
        /// </summary>
#if NET10_0_OR_GREATER
        [Obsolete("KeyExchangeAlgorithm, KeyExchangeStrength, CipherAlgorithm, CipherStrength, HashAlgorithm and HashStrength properties are obsolete. Use NegotiatedCipherSuite instead.", DiagnosticId = "SYSLIB0058", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
#endif
        HashAlgorithmType HashAlgorithm { get; }

        /// <summary>
        /// Gets the strength, in bits, of the negotiated message authentication algorithm.
        /// </summary>
#if NET10_0_OR_GREATER
        [Obsolete("KeyExchangeAlgorithm, KeyExchangeStrength, CipherAlgorithm, CipherStrength, HashAlgorithm and HashStrength properties are obsolete. Use NegotiatedCipherSuite instead.", DiagnosticId = "SYSLIB0058", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
#endif
        int HashStrength { get; }

        /// <summary>
        /// Gets the negotiated key exchange algorithm.
        /// </summary>
#if NET10_0_OR_GREATER
        [Obsolete("KeyExchangeAlgorithm, KeyExchangeStrength, CipherAlgorithm, CipherStrength, HashAlgorithm and HashStrength properties are obsolete. Use NegotiatedCipherSuite instead.", DiagnosticId = "SYSLIB0058", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
#endif
        ExchangeAlgorithmType KeyExchangeAlgorithm { get; }

        /// <summary>
        /// Gets the strength, in bits, of the negotiated key exchange algorithm.
        /// </summary>
#if NET10_0_OR_GREATER
        [Obsolete("KeyExchangeAlgorithm, KeyExchangeStrength, CipherAlgorithm, CipherStrength, HashAlgorithm and HashStrength properties are obsolete. Use NegotiatedCipherSuite instead.", DiagnosticId = "SYSLIB0058", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
#endif
        int KeyExchangeStrength { get; }
    }
}
