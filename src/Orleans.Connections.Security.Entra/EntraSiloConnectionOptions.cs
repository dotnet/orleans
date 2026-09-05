using System;
using System.Collections.Generic;

namespace Orleans.Configuration;

/// <summary>
/// Configures Microsoft Entra authentication for Orleans silo connections.
/// </summary>
public sealed class EntraSiloConnectionOptions
{
    /// <summary>
    /// Gets or sets the tenant-specific OpenID Connect authority.
    /// </summary>
    /// <remarks>
    /// The authority must use HTTPS and must not use a tenant-independent endpoint such as
    /// <c>common</c>, <c>organizations</c>, or <c>consumers</c>.
    /// </remarks>
    public Uri? Authority { get; set; }

    /// <summary>
    /// Gets or sets the cluster-qualified resource or scope identifier used to request a token.
    /// </summary>
    /// <remarks>
    /// The <c>/.default</c> suffix is added when requesting a token. This value is not a valid JWT audience.
    /// </remarks>
    public string? TokenScope { get; set; }

    /// <summary>
    /// Gets or sets the resource application's client-ID GUID.
    /// </summary>
    /// <remarks>
    /// This value is compared with the JWT <c>aud</c> claim. It is not a scope URI.
    /// </remarks>
    public string? ResourceApplicationId { get; set; }

    /// <summary>
    /// Gets additional exact JWT audiences which are accepted.
    /// </summary>
    /// <remarks>
    /// <see cref="ResourceApplicationId"/> is always accepted and is the normal Microsoft Entra v2 audience.
    /// A scope or resource identifier URI must not be added for an Entra v2 token.
    /// </remarks>
    public ISet<string> ValidAudiences { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the tenant identifiers which are accepted.
    /// </summary>
    public ISet<string> ValidTenantIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the client application identifiers which are authorized to connect.
    /// </summary>
    /// <remarks>
    /// When this allowlist and <see cref="AllowedServicePrincipalObjectIds"/> are both configured,
    /// a caller is authorized when either identity matches.
    /// </remarks>
    public ISet<string> AllowedClientIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the service-principal object identifiers which are authorized to connect.
    /// </summary>
    /// <remarks>
    /// When this allowlist and <see cref="AllowedClientIds"/> are both configured,
    /// a caller is authorized when either identity matches.
    /// </remarks>
    public ISet<string> AllowedServicePrincipalObjectIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the application roles, at least one of which must be present.
    /// </summary>
    /// <remarks>
    /// These roles authorize a caller but do not replace the exact cluster binding configured by
    /// <see cref="ClusterRole"/> or <see cref="ClusterClaimType"/>.
    /// </remarks>
    public ISet<string> RequiredRoles { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the asymmetric signing algorithms which are accepted.
    /// </summary>
    public ISet<string> AllowedAlgorithms { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256,
    };

    /// <summary>
    /// Gets the token versions which are accepted.
    /// </summary>
    public ISet<string> SupportedTokenVersions { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "1.0",
        "2.0",
    };

    /// <summary>
    /// Gets the additional hosts from which metadata or signing keys can be retrieved.
    /// </summary>
    /// <remarks>
    /// The authority host is always trusted. Additions should only be used when an identity
    /// provider's documented metadata endpoint uses another host in the same trusted cloud.
    /// Redirect responses are always rejected.
    /// </remarks>
    public ISet<string> AdditionalTrustedMetadataHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets a value indicating whether any application in a valid tenant is authorized.
    /// </summary>
    /// <remarks>The default is <see langword="false"/>.</remarks>
    public bool AllowAnyApplicationInTenant { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether delegated tokens can be accepted.
    /// </summary>
    /// <remarks>The default is <see langword="false"/>. Application tokens are required by default.</remarks>
    public bool AllowDelegatedTokens { get; set; }

    /// <summary>
    /// Gets or sets the claim whose value must exactly match the local Orleans cluster identifier.
    /// </summary>
    /// <remarks>Claim type and value matching is ordinal and exact.</remarks>
    public string? ClusterClaimType { get; set; }

    /// <summary>
    /// Gets or sets the exact application role required to connect to the local Orleans cluster.
    /// </summary>
    /// <remarks>
    /// Matching is ordinal and exact. For example, a silo role can be
    /// <c>Orleans.Silo.Connect.&lt;cluster-id&gt;</c>.
    /// </remarks>
    public string? ClusterRole { get; set; }

    /// <summary>
    /// Gets or sets a composite-format string used to construct a required cluster role.
    /// </summary>
    /// <remarks><c>{0}</c> is replaced with the local Orleans cluster identifier.</remarks>
    public string? ClusterRoleFormat { get; set; }

    /// <summary>
    /// Gets or sets an obsolete composite-format string which formerly constructed a cluster audience.
    /// </summary>
    /// <remarks>
    /// This property is retained for source compatibility and is not used to authorize a cluster.
    /// Configure <see cref="ResourceApplicationId"/> for JWT audience validation and use
    /// <see cref="ClusterRole"/> or <see cref="ClusterClaimType"/> for exact cluster authorization.
    /// </remarks>
    [Obsolete(
        $"Use {nameof(ResourceApplicationId)} with {nameof(ClusterRole)} or {nameof(ClusterClaimType)} instead. " +
        $"{nameof(TokenScope)} is not a JWT audience.")]
    public string? ClusterAudienceFormat { get; set; }

    /// <summary>
    /// Gets or sets the minimum remaining lifetime required for acquired and validated tokens.
    /// </summary>
    public TimeSpan MinimumRemainingTokenLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets the maximum accepted token lifetime, measured from <c>nbf</c> to <c>exp</c>.
    /// </summary>
    public TimeSpan MaximumTokenLifetime { get; set; } = TimeSpan.FromHours(2);

    /// <summary>
    /// Gets or sets the clock skew applied to <c>nbf</c> and <c>exp</c> validation.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets the maximum encoded JWT size.
    /// </summary>
    public int MaximumTokenSize { get; set; } = 16 * 1024;

    /// <summary>
    /// Gets or sets how often cached OpenID Connect metadata is automatically refreshed.
    /// </summary>
    public TimeSpan AutomaticMetadataRefreshInterval { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Gets or sets the minimum interval between refreshes caused by unknown signing keys.
    /// </summary>
    public TimeSpan UnknownSigningKeyRefreshInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the initial metadata refresh retry delay.
    /// </summary>
    public TimeSpan MetadataRefreshBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the maximum metadata refresh retry delay.
    /// </summary>
    public TimeSpan MaximumMetadataRefreshBackoff { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the maximum proportional jitter added to metadata refresh retry delays.
    /// </summary>
    public double MetadataRefreshJitterRatio { get; set; } = 0.2;

    /// <summary>
    /// Gets or sets the metadata retrieval timeout.
    /// </summary>
    public TimeSpan MetadataRetrievalTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets how long successfully validated metadata can be used during a metadata outage.
    /// </summary>
    /// <remarks>
    /// Last-known-good metadata is never used beyond this interval. This bounds how long a signing
    /// key which has been removed by the authority can continue to be trusted during an outage.
    /// </remarks>
    public TimeSpan LastKnownGoodLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Gets or sets the maximum metadata document size in bytes.
    /// </summary>
    public int MaximumMetadataSize { get; set; } = 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum number of callers which can wait for the single metadata refresh.
    /// </summary>
    public int MaximumMetadataRefreshQueueSize { get; set; } = 64;
}
