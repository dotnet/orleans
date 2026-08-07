using System;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Connections.Security;

/// <summary>
/// Controls enforcement of silo-to-silo connection authentication.
/// </summary>
public enum SiloConnectionAuthenticationMode
{
    /// <summary>Disables connection authentication.</summary>
    Disabled,

    /// <summary>Attempts authentication when supported and records failures without rejecting policy failures.</summary>
    Audit,

    /// <summary>Requires every silo connection to be authenticated.</summary>
    Required,
}
/// <summary>
/// Identifies the direction of a silo connection.
/// </summary>
public enum SiloConnectionAuthenticationDirection
{
    /// <summary>An inbound connection.</summary>
    Inbound,

    /// <summary>An outbound connection.</summary>
    Outbound,
}

/// <summary>
/// Identifies a bounded connection-authentication failure category.
/// </summary>
public enum SiloConnectionAuthenticationFailure
{
    /// <summary>No failure occurred.</summary>
    None,

    /// <summary>No token was supplied.</summary>
    MissingToken,

    /// <summary>The token was invalid.</summary>
    InvalidToken,

    /// <summary>The token was expired or did not have sufficient remaining lifetime.</summary>
    ExpiredToken,

    /// <summary>The caller was not authorized for this cluster.</summary>
    UnauthorizedCaller,

    /// <summary>The authentication provider was unavailable.</summary>
    ProviderUnavailable,

    /// <summary>Token validation failed unexpectedly.</summary>
    ValidationError,
}

/// <summary>
/// A bearer token used to authenticate an outbound silo connection.
/// </summary>
/// <param name="Value">The token value.</param>
/// <param name="ExpiresAt">The token expiration time.</param>
public readonly record struct SiloConnectionToken(string Value, DateTimeOffset? ExpiresAt);

/// <summary>
/// Supplies bearer tokens for outbound silo connections.
/// </summary>
public interface ISiloConnectionTokenProvider
{
    /// <summary>Gets a token for an outbound silo connection.</summary>
    ValueTask<SiloConnectionToken> GetTokenAsync(
        SiloConnectionTokenRequestContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Validates bearer tokens received on inbound silo connections.
/// </summary>
public interface ISiloConnectionTokenValidator
{
    /// <summary>Validates a token for an inbound silo connection.</summary>
    ValueTask<SiloConnectionTokenValidationResult> ValidateTokenAsync(
        string token,
        SiloConnectionTokenValidationContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Describes an outbound token request.
/// </summary>
public sealed class SiloConnectionTokenRequestContext
{
    internal SiloConnectionTokenRequestContext(string clusterId, EndPoint? localEndPoint, EndPoint? remoteEndPoint)
    {
        ClusterId = clusterId;
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
    }

    /// <summary>Gets the expected Orleans cluster identifier.</summary>
    public string ClusterId { get; }

    /// <summary>Gets the connection direction.</summary>
    public SiloConnectionAuthenticationDirection Direction => SiloConnectionAuthenticationDirection.Outbound;

    /// <summary>Gets the local endpoint, if available.</summary>
    public EndPoint? LocalEndPoint { get; }

    /// <summary>Gets the remote endpoint, if available.</summary>
    public EndPoint? RemoteEndPoint { get; }
}

/// <summary>
/// Describes the policy context for an inbound token validation.
/// </summary>
public sealed class SiloConnectionTokenValidationContext
{
    internal SiloConnectionTokenValidationContext(string clusterId, EndPoint? localEndPoint, EndPoint? remoteEndPoint)
    {
        ClusterId = clusterId;
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
    }

    /// <summary>Gets the expected Orleans cluster identifier.</summary>
    public string ClusterId { get; }

    /// <summary>Gets the connection direction.</summary>
    public SiloConnectionAuthenticationDirection Direction => SiloConnectionAuthenticationDirection.Inbound;

    /// <summary>Gets the local endpoint, if available.</summary>
    public EndPoint? LocalEndPoint { get; }

    /// <summary>Gets the remote endpoint, if available.</summary>
    public EndPoint? RemoteEndPoint { get; }
}

/// <summary>
/// The structured result of validating a silo connection token.
/// </summary>
public sealed class SiloConnectionTokenValidationResult
{
    private SiloConnectionTokenValidationResult(
        bool succeeded,
        ClaimsPrincipal? principal,
        DateTimeOffset? expiresAt,
        SiloConnectionAuthenticationFailure failure)
    {
        Succeeded = succeeded;
        Principal = principal;
        ExpiresAt = expiresAt;
        Failure = failure;
    }

    /// <summary>Gets whether validation succeeded.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the validated principal.</summary>
    public ClaimsPrincipal? Principal { get; }

    /// <summary>Gets the validated credential expiration time.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>Gets the failure category.</summary>
    public SiloConnectionAuthenticationFailure Failure { get; }

    /// <summary>Creates a successful validation result.</summary>
    public static SiloConnectionTokenValidationResult Success(ClaimsPrincipal principal, DateTimeOffset? expiresAt) =>
        new(true, principal ?? throw new ArgumentNullException(nameof(principal)), expiresAt, SiloConnectionAuthenticationFailure.None);

    /// <summary>Creates a failed validation result.</summary>
    public static SiloConnectionTokenValidationResult Fail(SiloConnectionAuthenticationFailure failure)
    {
        if (failure == SiloConnectionAuthenticationFailure.None)
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }

        return new(false, null, null, failure);
    }
}
