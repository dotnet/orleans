namespace Orleans.Connections.Security;

/// <summary>
/// Defines the versioned silo connection authentication wire protocol.
/// </summary>
public static class SiloConnectionAuthenticationProtocol
{
    /// <summary>
    /// The ALPN identifier for the token-frame and acknowledgment protocol.
    /// </summary>
    public const string Version2 = "Orleans1+TokenAuth2";
}
