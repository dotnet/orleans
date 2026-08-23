---
title: Authenticate Orleans connections
description: Authenticate silo and external client connections with TLS and Microsoft Entra workload identities.
ms.date: 08/23/2026
ms.topic: how-to
---

# Authenticate Orleans connections

Authenticated connections verify the workload identity of a connecting silo or
external Orleans client before Orleans reads its connection preamble or
application messages. Use
<xref:Orleans.Hosting.OrleansConnectionSecurityHostingExtensions.UseAuthenticatedSiloConnections*?displayProperty=nameWithType>
for silo traffic and
<xref:Orleans.Hosting.OrleansConnectionSecurityHostingExtensions.UseAuthenticatedClientConnections*?displayProperty=nameWithType>
on gateways and external clients. Each method configures TLS and bearer-token
authentication as one ordered policy.

> [!IMPORTANT]
> Silo and client connections are configured independently. Enabling
> authentication for one path doesn't silently change the other.

## Plan the secure topology

Start with a path-by-path policy. Don't treat "inside the cluster network" as a
single trust decision.

| Component or path | Trust requirement | Recommended controls |
|---|---|---|
| Silo-to-silo connection | Every admitted silo is trusted as part of this cluster | Private network policy, TLS or mTLS, resource-application GUID audience, exact cluster-specific silo role, silo caller allowlist |
| External client-to-gateway connection | Every admitted client is trusted to access the Orleans cluster | Private network policy, server-authenticated TLS or mTLS, resource-application GUID audience, exact cluster-specific client role, client caller allowlist |
| Public user traffic | End users and arbitrary upstream callers aren't inside the Orleans trust boundary | Authenticate and authorize at application ingress; don't expose an Orleans port as public ingress |
| Membership, storage, reminders, and streams | Configured providers and the data they return are trusted cluster infrastructure | Provider-native TLS, workload identity, least-privilege data-plane permissions, and administrative access controls |

Orleans has a coarse-grained trust boundary. If a silo or external Orleans
client can connect and authenticate, Orleans treats it as trusted. An admitted
client can invoke any grain interface available to it; Orleans connection
authentication isn't a per-grain or per-method authorization system. Therefore,
only admit application workloads which belong inside the same trust boundary as
the cluster. Authenticate and authorize untrusted end users before they reach an
Orleans client, and expose only application-specific operations through that
trusted client.

Configured storage, membership, reminder, and stream providers are trusted too.
Orleans assumes that provider responses and persisted data are authentic and
authorized for the cluster. Protect provider credentials, transport, data, and
administrative access accordingly. A malicious or compromised provider is
outside this connection-authentication threat model.

Don't expose the silo or gateway port to the public internet.

Install `Microsoft.Orleans.Connections.Security` in every silo and external
Orleans client. Install `Microsoft.Orleans.Connections.Security.Entra` in every
process which acquires or validates an Entra token. The Entra package handles
token acquisition and strict validation, including metadata and signing-key
rollover. Don't copy JWT parsing or validation logic into the application.

Before implementation, record the owner and expected value for each of these
items:

- `ServiceId` and environment-specific `ClusterId`.
- Silo and gateway DNS names, ports, and permitted network sources.
- Certificate issuers, SANs, EKUs, trust stores, and revocation endpoints.
- Entra tenant, cluster-qualified token request scope, resource application
  client-ID GUID, exact cluster roles, and caller application IDs.
- Credential and certificate rotation owners, alert thresholds, and emergency
  revocation procedure.

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
token decides whether the connecting workload is admitted to the Orleans trust
boundary. Membership still determines which silos make up the cluster.
Connection authentication doesn't propagate end-user identity, enforce
per-grain or per-method authorization, or prove that a workload owns the exact
`SiloAddress` it claims.

The design protects against network peers without an authorized workload
credential, unauthenticated downgrade in enforcement mode, cross-cluster token
reuse, and malformed or excessively concurrent authentication exchanges. It
doesn't protect against compromise of an admitted silo or client, malicious or
corrupted trusted storage, theft and replay of a bearer token before expiration,
or compromise of a trusted CA, identity provider, signing key, or host. Use
short-lived tokens, workload isolation, network policy, and optionally mTLS to
reduce the remaining risk.

## Provision Entra authorization

Audience validation alone isn't caller authorization. Keep these three values
separate:

| Token request scope | Entra v2 JWT audience | Exact cluster authorization |
|---|---|---|
| Register a cluster-qualified resource identifier such as `api://<resource-application-client-id-guid>/contoso-prod-westus`. Set it as `TokenScope` without `/.default`; Orleans appends that suffix only when acquiring a token. | Set `ResourceApplicationId` to the resource application's client-ID GUID. Microsoft Entra emits this GUID as `aud` in a v2 access token. | Require an exact role such as `Orleans.Silo.Connect.contoso-prod-westus` or `Orleans.Client.Connect.contoso-prod-westus`. Alternatively, require one explicit custom claim whose value exactly equals the local `ClusterId`. |

The cluster-qualified URI is never a successful JWT audience. For example, the
credential requests
`api://<resource-application-client-id-guid>/contoso-prod-westus/.default`,
but the resulting v2 JWT must contain
`"aud": "<resource-application-client-id-guid>"`.

Create the identity boundary in this order:

1. Create a resource application for the Orleans cluster security boundary.
2. Note its client-ID GUID, request v2 access tokens, and give each environment
   a distinct identifier URI which includes the `ClusterId`, for example
   `api://<resource-application-client-id-guid>/contoso-prod-westus`.
3. Define exact cluster-specific application roles
   `Orleans.Silo.Connect.contoso-prod-westus` and
   `Orleans.Client.Connect.contoso-prod-westus`, with applications as allowed
   member types.
4. Configure `idtyp` as an optional access-token claim so that application
   tokens include `idtyp: "app"`.
5. Create or select one workload identity for each independently deployable
   silo and client workload. Don't share a client secret or exported
   certificate across the fleet.
6. Assign only the matching application role. A client identity doesn't need
   the silo role.
7. Put each application ID in the matching caller allowlist. Role assignment
   and allowlisting are separate checks; require both.
8. Configure a managed identity, workload identity federation, or another
   non-interactive credential. Grant no Microsoft Graph permission merely to
   establish an Orleans connection.

This bounded manifest excerpt contains identifiers only. Generate stable GUIDs
for each `appRoles[].id`; they aren't credentials. App-role assignments are
made to workload service principals separately from this resource-application
manifest.

```json
{
  "appId": "<resource-application-client-id-guid>",
  "identifierUris": [
    "api://<resource-application-client-id-guid>/contoso-prod-westus"
  ],
  "api": {
    "requestedAccessTokenVersion": 2
  },
  "appRoles": [
    {
      "allowedMemberTypes": [ "Application" ],
      "description": "Connect a silo to contoso-prod-westus.",
      "displayName": "Orleans silo connect: contoso-prod-westus",
      "id": "<silo-cluster-role-guid>",
      "isEnabled": true,
      "value": "Orleans.Silo.Connect.contoso-prod-westus"
    },
    {
      "allowedMemberTypes": [ "Application" ],
      "description": "Connect an Orleans client to contoso-prod-westus.",
      "displayName": "Orleans client connect: contoso-prod-westus",
      "id": "<client-cluster-role-guid>",
      "isEnabled": true,
      "value": "Orleans.Client.Connect.contoso-prod-westus"
    }
  ],
  "optionalClaims": {
    "accessToken": [
      {
        "name": "idtyp",
        "essential": true,
        "additionalProperties": []
      }
    ]
  }
}
```

As an alternative to app-role cluster binding, a trusted issuer claims-mapping
policy can emit a signed custom claim such as
`orleans_cluster: "contoso-prod-westus"`. Set `ClusterClaimType` to
`orleans_cluster` instead of setting `ClusterRole`; don't configure both. The
validator compares the claim value to the local `ClusterId` using exact ordinal
matching. A general caller role doesn't replace either exact cluster-binding
mechanism.

Use a tenant-specific authority. Don't use `common`, `organizations`, or
`consumers`. Permit only application tokens issued to the expected tenant,
resource-application GUID audience, caller application, exact cluster role or
claim, and cluster binding. Keep access tokens short-lived and keep every
host's clock synchronized.

## Issue and deploy certificates

Use a CA and trust-store design which identifies the workload boundary, not
merely any machine with a publicly trusted certificate.

| Endpoint | Certificate requirements |
|---|---|
| Silo | Server Authentication EKU, DNS SAN matching the configured target host, and a protected private key |
| Silo when using mTLS between silos | Both Server Authentication and Client Authentication EKUs |
| External client when using bearer authentication with server-authenticated TLS | No client certificate; it still validates the gateway certificate |
| External client when using mTLS | A distinct certificate with Client Authentication EKU |

Install the issuing roots before deploying leaf certificates. Keep workload
trust stores narrow, and don't accept an arbitrary certificate from a broad
corporate or public root as proof of cluster membership. Restrict private-key
access to the process identity and load passwords from a secret provider rather
than ordinary configuration.

The gateway's DNS SAN must match the target host used by external clients. If
silo and gateway traffic use different DNS names or certificate policies,
configure their TLS policies independently instead of relying on the shared
<xref:Orleans.Hosting.OrleansConnectionSecurityHostingExtensions.UseTls*>
convenience API.

Prefer an explicit <xref:Azure.Core.TokenCredential> appropriate to the hosting
environment. The maintained sample supplies a `WorkloadIdentityCredential`; it
doesn't silently use a developer or unrelated cached identity:

:::code language="csharp" source="snippets/authenticated-silo-connections/csharp/ConnectionAuthenticationExamples.cs" id="ExplicitCredential":::

Create the credential once and reuse it. The credential implementation owns its
token cache.

## Configure silos and clients

The sample configures mTLS between silos and server-authenticated TLS plus
bearer authentication for external clients. Both paths use platform chain and
DNS-name validation with online revocation checking. Install only the expected
public or private roots in the platform trust store and overlap old and new
roots there during CA rotation. Each silo certificate therefore needs both the
Server Authentication and Client Authentication EKUs.

:::code language="csharp" source="snippets/authenticated-silo-connections/csharp/ConnectionAuthenticationExamples.cs" id="AuthenticatedSiloConnections":::

The configured `TargetHost` must match a DNS SAN and the chain must be valid
and trusted. Never replace this policy with
<xref:Orleans.Connections.Security.TlsOptions.AllowAnyRemoteCertificate*> or a
custom certificate-validation callback. `Required` mode rejects custom
certificate-validation callbacks and direct per-connection TLS authentication
callbacks during configuration.

The example deliberately bounds token bytes, exchange duration, concurrent
handshakes, and minimum remaining token lifetime. Keep all size, duration,
queue, concurrency, metadata-refresh, and token-lifetime limits finite.
Configuration is validated at startup; invalid middleware ordering, missing
TLS/provider registrations, and conflicting TLS policies fail closed.

The compiled configuration separates token acquisition, GUID audience
validation, and exact cluster authorization:

:::code language="csharp" source="snippets/authenticated-silo-connections/csharp/ConnectionAuthenticationExamples.cs" id="EntraAuthenticationOptions":::

Call
<xref:Orleans.Hosting.OrleansConnectionSecurityHostingExtensions.UseAuthenticatedSiloConnections*>
once on every silo. A silo both validates inbound silo tokens and acquires a
token for outbound silo connections, so it needs both a provider and validator.

### Authenticate external clients

Configure the gateway side on every silo. It validates client tokens before the
gateway reads the Orleans connection preamble:

:::code language="csharp" source="snippets/authenticated-silo-connections/csharp/ConnectionAuthenticationExamples.cs" id="AuthenticatedClientGateway":::

Configure each external Orleans client with the corresponding outbound policy:

:::code language="csharp" source="snippets/authenticated-silo-connections/csharp/ConnectionAuthenticationExamples.cs" id="AuthenticatedClient":::

The client and gateway must use compatible enforcement modes and the same Entra
token request scope, resource-application GUID, tenant, exact client cluster
role, and caller authorization. Keep the external-client role and allowlist
separate from the silo policy. The
<xref:Orleans.Connections.Security.SiloConnectionTokenRequestContext.Target> and
<xref:Orleans.Connections.Security.SiloConnectionTokenValidationContext.Target>
properties distinguish
client-to-gateway traffic from silo-to-silo traffic for custom providers.
After gateway authentication succeeds, Orleans trusts that client connection.
The authenticated principal isn't propagated into grain requests, and Orleans
doesn't apply per-grain or per-method authorization. If callers require
different permissions, enforce them before they enter the Orleans client or
implement an explicit application-level authorization design.

Call
<xref:Orleans.Hosting.OrleansConnectionSecurityHostingExtensions.UseAuthenticatedClientConnections*>
once on every gateway-hosting silo and once on every external client. The
gateway needs a validator; the external client needs a provider and an exact
TLS target host. When both policies run in one silo, Orleans keeps their Entra
options and token services isolated.

## Choose an enforcement mode

<xref:Orleans.Connections.Security.SiloConnectionAuthenticationMode> is
snapshotted at startup. Changing it requires a silo or client process restart.
Choose the mode independently for silo and client paths, but keep both ends of
each path rollout-compatible.

| Mode | Negotiation and acceptance behavior |
|---|---|
| `Disabled` | Advertises only the baseline Orleans protocol and doesn't exchange authentication frames. |
| `Audit` | Prefers authentication and permits measured, unauthenticated baseline fallback only with a peer which doesn't negotiate authentication. A failed negotiated authentication rejects the connection. |
| `Required` | Advertises only the authentication protocol and accepts only a successful authenticated result with a principal and, by default, a finite expiration. |

`Required` has no unauthenticated fallback. A `Required` silo and an old or
disabled silo have no common ALPN protocol, so TLS negotiation fails. A
`Required` outbound peer also rejects an Audit result which was accepted but
isn't authenticated.

After peers negotiate the authentication ALPN, every acquisition, validation,
authorization, expiry, malformed framing, acknowledgment, timeout, or overload
failure aborts the connection in both `Audit` and `Required`. `Audit` cannot
reinterpret a failed exchange as baseline Orleans traffic. Its baseline
fallback applies only when TLS negotiated the baseline ALPN with a peer which
doesn't support authentication.

## Plan for token expiration

Authentication occurs once per connection, but an Orleans connection can otherwise
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
2. Restart silos and clients by failure domain with `Audit`. Retain canaries and
   monitor baseline fallback, acquisition and validation failures,
   authorization denials, provider availability, latency, concurrency
   saturation, and metadata refresh.
3. Remain in `Audit` until every expected silo pair and external client path has
   negotiated authentication. Deliberately reconnect expected peers and
   representative clients, then verify each new connection authenticates,
   unexpected fallback and failure rates remain zero, and representative
   authenticated connections recycle at token expiry.
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

## Prove the policy fails closed

Run positive and negative connection tests in a non-production environment
before enabling `Required`. A successful happy-path connection alone doesn't
prove the boundary.

| Test | Expected result in `Required` | Expected result in `Audit` |
|---|---|---|
| Authorized silo and authorized external client | Connect and make representative grain calls | Connect, authenticate, and make representative grain calls |
| Missing token, malformed token, or user-delegated token after authentication negotiation | Connection rejected before the Orleans preamble | Connection rejected before the Orleans preamble |
| URI `aud` instead of the resource application GUID | Connection rejected | Connection rejected after authentication negotiation |
| Wrong tenant or issuer, missing or wrong exact cluster role/claim, or unlisted caller application ID | Connection rejected | Connection rejected after authentication negotiation |
| Expired token or token below the minimum remaining lifetime | Connection rejected | Connection rejected after authentication negotiation |
| Untrusted issuer, wrong DNS SAN, missing EKU, expired certificate, or revoked certificate | TLS handshake rejected | TLS handshake rejected |
| Peer using baseline Orleans ALPN only | TLS negotiation fails; no unauthenticated fallback | Baseline connection can continue unauthenticated and is recorded as fallback |
| Token provider, metadata endpoint, or signing-key refresh unavailable after authentication negotiation | New connection fails; mode remains `Required` | New connection fails; mode remains `Audit` |
| Handshake concurrency or queue limit exceeded | Excess work is rejected without unbounded growth | Excess work is rejected without unbounded growth |

Repeat the tests after certificate, federated-credential, app-role, audience,
and signing-key rotation. Include reconnects: an already open connection can
hide a broken credential until token-expiry recycling or a process restart.

## Monitor authentication

Export the `Microsoft.Orleans.Connections.Security` meter. The maintained
sample enables an OTLP exporter when `OTEL_EXPORTER_OTLP_ENDPOINT` is set:

:::code language="csharp" source="snippets/authenticated-silo-connections/csharp/ConnectionAuthenticationExamples.cs" id="FixedDiagnostics":::

Alert on rates and latency for these instruments:

| Instrument | Operational use |
|---|---|
| `orleans.connections.authentication.attempts` | Count outcomes by fixed result category; `result=overload` identifies authentication capacity exhaustion after both the concurrency and pending-queue limits are reached. |
| `orleans.connections.authentication.duration` | Detect token-provider, metadata, validation, or network latency. |
| `orleans.connections.authentication.active` | Track established authenticated connections by connection type and direction. |
| `orleans.connections.authentication.protocol_fallbacks` | Identify peers which haven't negotiated authentication in `Audit`. |

Keep dimensions bounded to direction, mode, protocol version, and fixed result
category. The `connection.type` dimension distinguishes `silo` and `client`
connections. Never add token, tenant, client, object, issuer, endpoint, or arbitrary
exception values as metric tags.

Authentication logs use fixed event IDs and bounded categories such as
overload, timeout, protocol error, TLS policy error, acquisition failure,
validation failure, authorization failure, and expiration. Preserve event ID,
connection type, category, direction, and mode in the log pipeline. Tokens must
never appear in logs, traces, metrics, activities, exceptions, or connection
features.

## Operate and recover

Maintain runbooks for these events:

- **Certificate rotation:** Trust the replacement issuer first, overlap old and
  new chains, deploy new leaves, force representative reconnects, and only then
  remove the old trust root.
- **Workload credential rotation:** Create the replacement credential or
  federation before removing the old one. Verify a newly opened connection,
  not only an existing connection.
- **Signing-key rollover:** Keep metadata refresh healthy and alert on repeated
  unknown-key refresh failures. Don't pin one issuer signing key in
  application code.
- **Caller removal:** Remove the role assignment and allowlist entry, then
  recycle active connections if access must end before their validated token
  expiration.
- **Credential or private-key compromise:** Block the workload at the network
  boundary, revoke or disable the credential, remove authorization, rotate
  affected keys and certificates, and restart or recycle connections. Don't
  automatically change enforcement to `Audit` or `Disabled`.
- **Identity-provider outage:** Existing authenticated connections can
  continue until recycled. New connections fail closed in `Required`. Use
  capacity and availability planning rather than an automatic security
  downgrade.

## Production checklist

- Use an explicit workload credential and keep its federated token or secret
  material out of source and ordinary configuration.
- Give each cluster/environment a cluster-qualified token request scope, use the
  resource application client-ID GUID as the v2 audience, use separate exact
  silo and client cluster roles, and require both the matching role and caller
  allowlist.
- Keep TLS 1.2 or later, certificate chain/name checks, revocation policy, and
  narrow trust roots enabled.
- Bound token, timeout, concurrency, queue, metadata refresh, and token lifetime
  settings.
- Restrict silo and gateway ports with network policy.
- Admit only silos and clients which belong inside the cluster trust boundary;
  don't use a direct Orleans client as an untrusted public endpoint.
- Treat configured storage and providers as trusted infrastructure, and protect
  their credentials, transport, data, and administrative access.
- Synchronize clocks and exercise certificate, key, and identity rotation.
- Treat unexpected baseline fallback and every failed negotiated
  authentication in `Audit`, and every authentication failure in `Required`,
  as an operational event.
- Test wrong-certificate, wrong-identity, provider-outage, overload, rotation,
  reconnect, and rollback scenarios before production.

## See also

- [Authenticated silo connections sample](https://github.com/dotnet/orleans/tree/main/samples/AuthenticatedSiloConnections)
- [Secure Orleans connections with TLS](transport-layer-security.md)
- [Monitor an Orleans application](monitoring/index.md)
- <xref:Orleans.Connections.Security.ISiloConnectionAuthenticationFeature>
- [Azure Identity client library for .NET](https://learn.microsoft.com/dotnet/azure/sdk/authentication/)
- [Application roles in Microsoft Entra ID](https://learn.microsoft.com/entra/identity-platform/howto-add-app-roles-in-apps)
