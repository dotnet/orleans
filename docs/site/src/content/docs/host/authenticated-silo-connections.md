---
title: Authenticate Orleans silo connections
description: Authenticate silo-to-silo connections with TLS and Microsoft Entra workload identities.
ms.date: 08/07/2026
ms.topic: how-to
---

# Authenticate Orleans silo connections

Authenticated silo connections verify the workload identity of a connecting
silo before Orleans reads its connection preamble or application messages. Use
<xref:Orleans.Hosting.OrleansConnectionSecurityHostingExtensions.UseAuthenticatedSiloConnections*?displayProperty=nameWithType>
to configure TLS and bearer-token authentication as one ordered policy.

> [!IMPORTANT]
> This feature applies only to silo-to-silo connections. Client-to-gateway
> behavior is unchanged. Secure gateway traffic with the existing
> [TLS](transport-layer-security.md) and application authentication mechanisms.

Install `Microsoft.Orleans.Connections.Security` and
`Microsoft.Orleans.Connections.Security.Entra` in every silo. The Entra package
acquires and validates tokens, including metadata and signing-key rollover.
Don't copy JWT parsing or validation logic into the application.

## Understand the security boundary

The connection pipeline is:

```text
TCP
  -> TLS handshake and ALPN negotiation
  -> bearer-token exchange
  -> Orleans connection preamble
  -> Orleans messages
```

TLS protects the bearer token in transit and authenticates the TLS server. The
token authenticates and authorizes the connecting workload. Membership still
determines which silos make up the cluster; connection authentication doesn't
replace membership, authorize individual grain calls, propagate end-user
identity, or prove that a workload owns the exact `SiloAddress` it claims.

The design protects against network peers without an authorized workload
credential, unauthenticated downgrade in enforcement mode, cross-cluster token
reuse, and malformed or excessively concurrent authentication exchanges. It
doesn't protect against compromise of an authorized silo, theft and replay of a
bearer token before expiration, or compromise of a trusted CA, identity
provider, signing key, or host. Use short-lived tokens, workload isolation,
network policy, and optionally mTLS to reduce the remaining risk.

## Bind authorization to one cluster and environment

Audience validation alone isn't caller authorization. Configure all of the
following:

1. A tenant-specific authority.
2. A dedicated audience for one cluster and deployment environment, such as
   `api://<resource-application-id>/contoso-prod-westus`.
3. The application role `Orleans.Silo.Connect`.
4. An explicit allowlist of caller application IDs.

The audience must exactly match the resource identifier registered in Microsoft
Entra. Don't remove the `api://` prefix or share a general-purpose silo audience
across environments. If the audience must be shared, require a separate
cluster-specific claim or role and compare it exactly to the local `ClusterId`.

Prefer an explicit <xref:Azure.Core.TokenCredential> appropriate to the hosting
environment. The maintained sample supplies a `WorkloadIdentityCredential`; it
doesn't silently use a developer or unrelated cached identity:

:::code language="csharp" source="../../../../../../samples/AuthenticatedSiloConnections/Program.cs" id="ExplicitCredential":::

Create the credential once and reuse it. The credential implementation owns its
token cache.

## Configure TLS and Entra authentication

The sample configures mTLS, platform chain and DNS-name validation, and online
revocation checking. Install only the expected public or private roots in the
platform trust store and overlap old and new roots there during CA rotation.
Each silo certificate therefore needs both the Server Authentication and
Client Authentication EKUs.

:::code language="csharp" source="../../../../../../samples/AuthenticatedSiloConnections/SiloAuthentication.cs" id="AuthenticatedSiloConnections":::

The configured `TargetHost` must match a DNS SAN and the chain must be valid
and trusted. Never replace this policy with
<xref:Orleans.Connections.Security.TlsOptions.AllowAnyRemoteCertificate*> or a
custom certificate-validation callback. `Required` mode rejects custom
certificate-validation callbacks during startup.

The example deliberately bounds token bytes, exchange duration, concurrent
handshakes, and minimum remaining token lifetime. Keep all size, duration,
queue, concurrency, metadata-refresh, and token-lifetime limits finite.
Configuration is validated at startup; invalid middleware ordering, missing
TLS/provider registrations, and conflicting TLS policies fail closed.

## Choose an enforcement mode

<xref:Orleans.Connections.Security.SiloConnectionAuthenticationMode> is
snapshotted at startup. Changing it requires a silo restart.

| Mode | Negotiation and acceptance behavior |
|---|---|
| `Disabled` | Advertises only the baseline Orleans protocol and doesn't exchange authentication frames. |
| `Audit` | Prefers authentication, permits baseline negotiation with an older or disabled peer, and accepts measured authentication failures. |
| `Required` | Advertises only the authentication protocol and accepts only a successful authenticated result with a principal and, by default, a finite expiration. |

`Required` has no unauthenticated fallback. A `Required` silo and an old or
disabled silo have no common ALPN protocol, so TLS negotiation fails. A
`Required` outbound peer also rejects an Audit result which was accepted but
isn't authenticated.

After peers negotiate the authentication ALPN, malformed framing,
acknowledgment, timeout, or overload failures abort the connection in every
mode. `Audit` can explicitly accept token acquisition, validation,
authorization, or provider failures as unauthenticated, but it cannot
reinterpret them as baseline Orleans traffic. Baseline fallback is permitted
only when TLS negotiated the baseline ALPN with a peer which doesn't support
authentication.

## Plan for token expiration

Authentication occurs once per connection, but a silo connection can otherwise
outlive its access token. In `Required` mode, Orleans uses the validator's
finite expiration and recycles the connection before expiry using a safety
margin and bounded jitter. Reconnection acquires a new token through the
caller-supplied credential.

The provider's expiration is advisory; it can't extend the expiration validated
by the receiving silo. Tokens without a finite expiration are rejected in
`Required` unless non-expiring credentials were explicitly enabled. Monitor
recycling before enforcement so a credential, metadata, or network problem
doesn't surface only when many connections approach expiration.

## Roll out safely

Define gates and ownership before changing modes:

1. Deploy the code everywhere with `Disabled` and restart the fleet.
2. Restart by failure domain with `Audit`. Retain canaries and monitor baseline
   fallback, acquisition and validation failures, authorization denials,
   provider availability, latency, concurrency saturation, and metadata
   refresh.
3. Remain in `Audit` until every expected silo pair has negotiated
   authentication. Deliberately reconnect every expected peer pair and verify
   each new connection authenticates, unexpected fallback and failure rates
   remain zero, and representative authenticated connections recycle at token
   expiry.
4. Restart `Required` canaries. Verify connectivity, membership stability,
   token renewal, and provider health before proceeding through each failure
   domain.

Use rates and denominators rather than raw cumulative counts for promotion
decisions. An identity-provider or metadata outage must not automatically
downgrade the cluster.

### Roll back

Don't roll directly from `Required` to `Disabled` one silo at a time; those
modes have no common ALPN. Use this fleet-wide, restart-based sequence:

```text
Required -> Audit across the fleet -> Disabled across the fleet
```

`Required` and `Audit` share the authentication ALPN, so the first step remains
wire-compatible. Document who may authorize the downgrade, how restarts are
coordinated, and the maximum accepted exposure window in `Audit`.

## Monitor authentication

Export the `Microsoft.Orleans.Connections.Security` meter. The maintained
sample enables an OTLP exporter when `OTEL_EXPORTER_OTLP_ENDPOINT` is set:

:::code language="csharp" source="../../../../../../samples/AuthenticatedSiloConnections/Program.cs" id="FixedDiagnostics":::

Alert on rates and latency for these instruments:

| Instrument | Operational use |
|---|---|
| `orleans.connections.authentication.attempts` | Count outcomes by fixed result category. |
| `orleans.connections.authentication.duration` | Detect token-provider, metadata, validation, or network latency. |
| `orleans.connections.authentication.active` | Detect handshake concurrency saturation. |
| `orleans.connections.authentication.protocol_fallbacks` | Identify peers which haven't negotiated authentication in `Audit`. |

Keep dimensions bounded to direction, mode, protocol version, and fixed result
category. Never add token, tenant, client, object, issuer, endpoint, or arbitrary
exception values as metric tags.

Authentication logs use fixed event IDs and bounded categories such as
overload, timeout, protocol error, TLS policy error, acquisition failure,
validation failure, authorization failure, and expiration. Preserve event ID,
category, direction, and mode in the log pipeline. Tokens must never appear in
logs, traces, metrics, activities, exceptions, or connection features.

## Production checklist

- Use an explicit workload credential and keep its federated token or secret
  material out of source and ordinary configuration.
- Give each cluster/environment an exact audience and require both a role and
  caller allowlist.
- Keep TLS 1.2 or later, certificate chain/name checks, revocation policy, and
  narrow trust roots enabled.
- Bound token, timeout, concurrency, queue, metadata refresh, and token lifetime
  settings.
- Restrict silo and gateway ports with network policy.
- Synchronize clocks and exercise certificate, key, and identity rotation.
- Treat unexpected baseline fallback in `Audit` and every authentication
  failure in `Required` as an operational event.

## See also

- [Authenticated silo connections sample](https://github.com/dotnet/orleans/tree/main/samples/AuthenticatedSiloConnections)
- [Secure Orleans connections with TLS](transport-layer-security.md)
- [Monitor an Orleans application](monitoring/index.md)
- <xref:Orleans.Connections.Security.ISiloConnectionAuthenticationFeature>
- [Azure Identity client library for .NET](https://learn.microsoft.com/dotnet/azure/sdk/authentication/)
- [Application roles in Microsoft Entra ID](https://learn.microsoft.com/entra/identity-platform/howto-add-app-roles-in-apps)
