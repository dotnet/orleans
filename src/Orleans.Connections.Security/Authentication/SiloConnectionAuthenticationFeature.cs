using System;
using System.Linq;
using System.Security.Claims;

namespace Orleans.Connections.Security;

/// <summary>
/// Describes the authentication state of a silo connection.
/// </summary>
public interface ISiloConnectionAuthenticationFeature
{
    /// <summary>Gets whether authentication was attempted.</summary>
    bool AuthenticationAttempted { get; }

    /// <summary>Gets whether the connection was authenticated.</summary>
    bool IsAuthenticated { get; }

    /// <summary>Gets an isolated copy of the authenticated principal.</summary>
    ClaimsPrincipal? Principal { get; }

    /// <summary>Gets the credential expiration time.</summary>
    DateTimeOffset? ExpiresAt { get; }

    /// <summary>Gets the authentication failure category.</summary>
    SiloConnectionAuthenticationFailure Failure { get; }

    /// <summary>Gets the negotiated authentication protocol.</summary>
    string Protocol { get; }
}

internal sealed class SiloConnectionAuthenticationFeature : ISiloConnectionAuthenticationFeature
{
    private readonly ClaimsPrincipal? _principal;

    public SiloConnectionAuthenticationFeature(
        bool authenticationAttempted,
        bool isAuthenticated,
        ClaimsPrincipal? principal,
        DateTimeOffset? expiresAt,
        SiloConnectionAuthenticationFailure failure,
        string protocol)
    {
        AuthenticationAttempted = authenticationAttempted;
        IsAuthenticated = isAuthenticated;
        _principal = principal is null ? null : ClonePrincipal(principal);
        ExpiresAt = expiresAt;
        Failure = failure;
        Protocol = protocol;
    }

    public bool AuthenticationAttempted { get; }

    public bool IsAuthenticated { get; }

    public ClaimsPrincipal? Principal => _principal is null ? null : ClonePrincipal(_principal);

    public DateTimeOffset? ExpiresAt { get; }

    public SiloConnectionAuthenticationFailure Failure { get; }

    public string Protocol { get; }

    private static ClaimsPrincipal ClonePrincipal(ClaimsPrincipal principal)
    {
        var identities = new ClaimsIdentity[principal.Identities.Count()];
        var index = 0;
        foreach (var identity in principal.Identities)
        {
            identities[index++] = identity.Clone();
        }

        return new ClaimsPrincipal(identities);
    }
}
