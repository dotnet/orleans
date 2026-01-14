using System;
using System.Net.Security;
using System.Security.Authentication;

namespace Orleans.Connections.Security
{
    public interface ITlsHandshakeFeature
    {
        SslProtocols Protocol { get; }

        /// <summary>
        /// Gets the <see cref="TlsCipherSuite"/>.
        /// </summary>
        TlsCipherSuite? NegotiatedCipherSuite => null;

        /// <summary>
        /// Gets the host name from the "server_name" (SNI) extension of the client hello if present.
        /// </summary>
        string HostName => string.Empty;

#if NET10_0_OR_GREATER
        [Obsolete("KeyExchangeAlgorithm, KeyExchangeStrength, CipherAlgorithm, CipherStrength, HashAlgorithm and HashStrength properties are obsolete. Use NegotiatedCipherSuite instead.", DiagnosticId = "SYSLIB0058", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
#endif
        CipherAlgorithmType CipherAlgorithm { get; }

#if NET10_0_OR_GREATER
        [Obsolete("KeyExchangeAlgorithm, KeyExchangeStrength, CipherAlgorithm, CipherStrength, HashAlgorithm and HashStrength properties are obsolete. Use NegotiatedCipherSuite instead.", DiagnosticId = "SYSLIB0058", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
#endif
        int CipherStrength { get; }

#if NET10_0_OR_GREATER
        [Obsolete("KeyExchangeAlgorithm, KeyExchangeStrength, CipherAlgorithm, CipherStrength, HashAlgorithm and HashStrength properties are obsolete. Use NegotiatedCipherSuite instead.", DiagnosticId = "SYSLIB0058", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
#endif
        HashAlgorithmType HashAlgorithm { get; }

#if NET10_0_OR_GREATER
        [Obsolete("KeyExchangeAlgorithm, KeyExchangeStrength, CipherAlgorithm, CipherStrength, HashAlgorithm and HashStrength properties are obsolete. Use NegotiatedCipherSuite instead.", DiagnosticId = "SYSLIB0058", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
#endif
        int HashStrength { get; }

#if NET10_0_OR_GREATER
        [Obsolete("KeyExchangeAlgorithm, KeyExchangeStrength, CipherAlgorithm, CipherStrength, HashAlgorithm and HashStrength properties are obsolete. Use NegotiatedCipherSuite instead.", DiagnosticId = "SYSLIB0058", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
#endif
        ExchangeAlgorithmType KeyExchangeAlgorithm { get; }

#if NET10_0_OR_GREATER
        [Obsolete("KeyExchangeAlgorithm, KeyExchangeStrength, CipherAlgorithm, CipherStrength, HashAlgorithm and HashStrength properties are obsolete. Use NegotiatedCipherSuite instead.", DiagnosticId = "SYSLIB0058", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
#endif
        int KeyExchangeStrength { get; }
    }
}
