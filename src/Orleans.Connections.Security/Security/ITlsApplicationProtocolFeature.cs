using System;

namespace Orleans.Connections.Security
{
    /// <summary>
    /// Provides access to the application protocol negotiated during the TLS handshake.
    /// </summary>
    public interface ITlsApplicationProtocolFeature
    {
        /// <summary>
        /// Gets the negotiated application protocol bytes.
        /// </summary>
        ReadOnlyMemory<byte> ApplicationProtocol { get; }
    }
}
