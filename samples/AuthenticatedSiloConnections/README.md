# Authenticated silo connections

This sample configures mutual TLS (mTLS) and Microsoft Entra workload
authentication for silo-to-silo connections, plus server-authenticated TLS and
Entra authentication for external client-to-gateway connections. It uses an
explicit `WorkloadIdentityCredential`; it doesn't construct
`DefaultAzureCredential` or copy JWT validation logic into the application.

The sample is a two-process localhost cluster. Start one process with the
default ports, then start another with
`OrleansSecurity__SiloPort=11112` and
`OrleansSecurity__GatewayPort=30001`. Both processes use the primary silo port
`11111`.

## Configure Microsoft Entra

Keep these three values separate:

| Purpose | Sample value |
|---|---|
| Cluster-qualified resource identifier used only for token acquisition | `api://<resource-application-client-id-guid>/contoso-prod-westus`; configure this as `TokenScope`, without `/.default` |
| Resource application client ID emitted as the Entra v2 JWT `aud` | `<resource-application-client-id-guid>`; configure this GUID as `ResourceApplicationId` |
| Exact cluster authorization | `Orleans.Silo.Connect.contoso-prod-westus` for silos or `Orleans.Client.Connect.contoso-prod-westus` for clients |

The package appends `/.default` when requesting a token, so the credential
requests
`api://<resource-application-client-id-guid>/contoso-prod-westus/.default`.
For a v2 access token, Microsoft Entra emits the resource application's
client-ID GUID—not that URI—as `aud`. The cluster-qualified URI is never a
successful JWT audience.

1. Register a resource application for the cluster security boundary and note
   its client-ID GUID.
2. Configure the identifier URI
   `api://<resource-application-client-id-guid>/contoso-prod-westus`.
3. Define the exact application roles
   `Orleans.Silo.Connect.contoso-prod-westus` and
   `Orleans.Client.Connect.contoso-prod-westus`, allowing applications as
   members.
4. Configure `idtyp` as an optional access-token claim so that application
   tokens include `idtyp: "app"`.
5. Assign only the matching role to each authorized silo or client workload
   identity.
6. Configure a federated identity credential for each workload and place its
   application ID in the matching silo or external-client allowlist.

This bounded resource-application manifest excerpt uses placeholders only.
Generate and retain stable GUIDs for each `appRoles[].id`; those GUIDs aren't
credentials.

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

The exact GUID audience, tenant, application-token classification, caller
application ID, and cluster role are validated by
`Microsoft.Orleans.Connections.Security.Entra`. Don't replace that package with
sample-owned JWT parsing or validation.

As an alternative cluster binding, an issuer-managed claims policy can emit a
signed custom claim such as `orleans_cluster: "contoso-prod-westus"`. Configure
`ClusterClaimType = "orleans_cluster"` instead of `ClusterRole`; don't require
both. The claim value must exactly equal the local `ClusterId`. The maintained
sample uses app roles because their manifest and assignments are explicit.

An authenticated silo or external Orleans client is inside the Orleans trust
boundary. Orleans doesn't apply per-grain or per-method authorization to that
connection. Admit only trusted application workloads, and authenticate and
authorize untrusted end users before their requests reach an Orleans client.
Configured storage and other providers are trusted cluster infrastructure.

The sample uses one resource application but separate cluster-specific
application roles and caller allowlists for silo and external-client traffic.
Use distinct resource applications as well if those paths have different
administrators or compromise boundaries.

## Configure TLS

Provide a PFX whose certificate has the Server Authentication and Client
Authentication EKUs and a DNS SAN matching `Certificate:TargetHost`. Install
the issuing private root in the operating-system trust store. The sample
requires successful platform chain, validity, EKU, and revocation checks in
both directions and explicitly configures the outbound DNS-name check. Overlap
the old and new roots in the platform trust store during CA rotation.

Supply the PFX password through a secret provider or the environment variable
`OrleansSecurity__Certificate__Password`; don't store it in `appsettings.json`.

## Run and observe

Use environment variables or a secret-aware configuration provider to replace
every placeholder in `appsettings.json`. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to
export the `Microsoft.Orleans.Connections.Security` meter. Structured console
logs preserve the runtime's fixed event IDs and bounded authentication result
categories.

Start in `Audit` mode. `Audit` permits unauthenticated baseline fallback only
when a peer doesn't negotiate the authentication protocol. Once peers negotiate
authentication, `Audit` fails closed just like `Required`: token acquisition,
validation, authorization, expiry, framing, timeout, and overload failures
reject the connection. Before proceeding to `Required`, deliberately reconnect
every expected silo pair and verify that each new connection authenticates,
baseline fallback and unexpected failure rates remain zero, and token-expiry
recycling succeeds for authenticated connections. Changing modes requires a
restart.

Before production, also verify that connections fail for an untrusted
certificate, wrong DNS SAN, a URI `aud` instead of the resource application
GUID, wrong tenant or issuer, a missing or wrong exact cluster role, an unlisted
caller application ID, and an expired token. Those failures must reject the
connection in `Required` and after authentication is negotiated in `Audit`. A
peer which supports only the baseline Orleans ALPN is rejected in `Required`
but can use the measured, unauthenticated baseline fallback in `Audit`. Repeat
the checks after certificate and identity rotation.

`Required` has no unauthenticated fallback. Roll back fleet-wide from
`Required` to `Audit`, and only then from `Audit` to `Disabled`. Never
automatically weaken the mode because Microsoft Entra or metadata is
unavailable.

The gateway validates external client bearer tokens using a distinct
cluster-specific `Orleans.Client.Connect.<cluster-id>` role and caller
allowlist. External clients must call
`UseAuthenticatedClientConnections` with a token provider and the same exact
resource application GUID, token request scope, tenant, and client cluster role.

See the maintained [authenticated Orleans connections
guide](../../docs/site/src/content/docs/host/authenticated-silo-connections.md)
for the complete production setup, rollout, validation, monitoring, rotation,
and incident-response guidance.
