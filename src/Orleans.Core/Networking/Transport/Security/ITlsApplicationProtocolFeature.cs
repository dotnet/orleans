#nullable enable

using System;

namespace Orleans.Connections.Transport.Security;

/// <summary>
/// Provides access to the negotiated TLS application protocol.
/// </summary>
public interface ITlsApplicationProtocolFeature
{
    /// <summary>
    /// Gets the negotiated TLS application protocol.
    /// </summary>
    ReadOnlyMemory<byte> ApplicationProtocol { get; }
}
