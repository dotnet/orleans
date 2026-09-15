using System;

namespace Orleans.Connections.Security;

/// <summary>
/// Configures Orleans connection authentication.
/// </summary>
public sealed class SiloConnectionAuthenticationOptions
{
    /// <summary>
    /// Gets or sets the authentication enforcement mode. Audit mode permits baseline protocol fallback,
    /// but does not permit a failed negotiated authentication exchange to continue.
    /// </summary>
    public SiloConnectionAuthenticationMode Mode { get; set; } = SiloConnectionAuthenticationMode.Required;

    /// <summary>Gets or sets the total token exchange timeout.</summary>
    public TimeSpan TokenExchangeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the maximum UTF-8 token size in bytes.</summary>
    public int MaxTokenSize { get; set; } = 16 * 1024;

    /// <summary>Gets or sets the maximum concurrent inbound authentication operations.</summary>
    public int MaxConcurrentInboundAuthentications { get; set; } = 256;

    /// <summary>Gets or sets the maximum concurrent outbound authentication operations.</summary>
    public int MaxConcurrentOutboundAuthentications { get; set; } = 256;

    /// <summary>Gets or sets the maximum queued inbound authentication operations.</summary>
    public int MaxPendingInboundAuthentications { get; set; } = 256;

    /// <summary>Gets or sets the maximum queued outbound authentication operations.</summary>
    public int MaxPendingOutboundAuthentications { get; set; } = 256;

    /// <summary>Gets or sets the minimum acceptable remaining credential lifetime.</summary>
    public TimeSpan MinimumRemainingTokenLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets or sets how long before credential expiration an authenticated connection is closed.</summary>
    public TimeSpan ExpirationSafetyMargin { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the maximum deterministic per-connection expiration jitter.</summary>
    public TimeSpan ExpirationJitter { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets whether credentials without a finite expiration are accepted.</summary>
    public bool AllowNonExpiringCredentials { get; set; }

    /// <summary>Gets or sets the expected TLS server DNS identity and SNI name.</summary>
    public string? TargetHost { get; set; }

    /// <summary>Gets or sets the time provider used for timeouts and expiration.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}
