using System;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Connections.Security;

/// <summary>
/// Controls enforcement of Orleans connection authentication.
/// </summary>
public enum SiloConnectionAuthenticationMode
{
    /// <summary>Disables connection authentication.</summary>
    Disabled,

    /// <summary>
    /// Allows a peer which did not negotiate token authentication to continue unauthenticated.
    /// Once token authentication is negotiated, any authentication failure rejects the connection.
    /// </summary>
    Audit,

    /// <summary>Requires every configured connection to be authenticated.</summary>
    Required,
}

/// <summary>
/// Identifies the kind of Orleans connection being authenticated.
/// </summary>
public enum SiloConnectionAuthenticationTarget
{
    /// <summary>A connection between silos.</summary>
    Silo,

    /// <summary>A connection between an external Orleans client and a silo gateway.</summary>
    Client,
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
/// Categories do not contain token, claim, or other peer-controlled values.
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
/// A bearer token used to authenticate an outbound Orleans connection.
/// </summary>
/// <param name="Value">The token value.</param>
/// <param name="ExpiresAt">The token expiration time.</param>
public readonly record struct SiloConnectionToken(string Value, DateTimeOffset? ExpiresAt)
{
    /// <inheritdoc />
    public override string ToString()
    {
        var expiration = ExpiresAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "null";
        return $"{nameof(SiloConnectionToken)} {{ Value = [REDACTED], ExpiresAt = {expiration} }}";
    }
}

/// <summary>
/// Supplies bearer tokens for outbound Orleans connections.
/// </summary>
public interface ISiloConnectionTokenProvider
{
    /// <summary>Gets a token for an outbound Orleans connection.</summary>
    ValueTask<SiloConnectionToken> GetTokenAsync(
        SiloConnectionTokenRequestContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Validates bearer tokens received on inbound Orleans connections.
/// </summary>
public interface ISiloConnectionTokenValidator
{
    /// <summary>Validates a token for an inbound Orleans connection.</summary>
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
    internal SiloConnectionTokenRequestContext(
        string clusterId,
        SiloConnectionAuthenticationTarget target,
        EndPoint? localEndPoint,
        EndPoint? remoteEndPoint)
    {
        ClusterId = clusterId;
        Target = target;
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
    }

    /// <summary>Gets the expected Orleans cluster identifier.</summary>
    public string ClusterId { get; }

    /// <summary>Gets the kind of connection being authenticated.</summary>
    public SiloConnectionAuthenticationTarget Target { get; }

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
    internal SiloConnectionTokenValidationContext(
        string clusterId,
        SiloConnectionAuthenticationTarget target,
        EndPoint? localEndPoint,
        EndPoint? remoteEndPoint)
    {
        ClusterId = clusterId;
        Target = target;
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
    }

    /// <summary>Gets the expected Orleans cluster identifier.</summary>
    public string ClusterId { get; }

    /// <summary>Gets the kind of connection being authenticated.</summary>
    public SiloConnectionAuthenticationTarget Target { get; }

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

    /// <summary>
    /// Gets the bounded failure category. This value does not expose token or claim content.
    /// </summary>
    public SiloConnectionAuthenticationFailure Failure { get; }

    /// <summary>Creates a successful validation result.</summary>
    public static SiloConnectionTokenValidationResult Success(ClaimsPrincipal principal, DateTimeOffset? expiresAt) =>
        new(true, principal ?? throw new ArgumentNullException(nameof(principal)), expiresAt, SiloConnectionAuthenticationFailure.None);

    /// <summary>
    /// Creates a failed validation result using a bounded category which does not contain remote token or claim content.
    /// </summary>
    public static SiloConnectionTokenValidationResult Fail(SiloConnectionAuthenticationFailure failure)
    {
        if (failure == SiloConnectionAuthenticationFailure.None)
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }

        return new(false, null, null, failure);
    }
}
