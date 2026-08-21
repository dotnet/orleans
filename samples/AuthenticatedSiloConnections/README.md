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

1. Register a resource application for the cluster security boundary.
2. Configure the identifier URI
   `api://<resource-application-id>/<cluster-id>`. The cluster ID includes the
   deployment environment, for example `contoso-prod-westus`.
3. Define the application roles `Orleans.Silo.Connect` and
   `Orleans.Client.Connect`, and allow applications as members.
4. Configure `idtyp` as an optional access-token claim so that application
   tokens include `idtyp: "app"`.
5. Assign only the matching role to each authorized silo or client workload
   identity.
6. Configure a federated identity credential for each workload and place its
   application ID in the matching silo or external-client allowlist.

The exact audience, tenant, application-token classification, caller
application ID, and application role are validated by
`Microsoft.Orleans.Connections.Security.Entra`. Don't replace that package with
sample-owned JWT parsing or validation.

An authenticated silo or external Orleans client is inside the Orleans trust
boundary. Orleans doesn't apply per-grain or per-method authorization to that
connection. Admit only trusted application workloads, and authenticate and
authorize untrusted end users before their requests reach an Orleans client.
Configured storage and other providers are trusted cluster infrastructure.

The sample uses one resource application but separate application roles and
caller allowlists for silo and external-client traffic. Use distinct resource
applications or audiences as well if those paths have different administrators
or compromise boundaries.

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

Start in `Audit` mode. Before proceeding to `Required`, deliberately reconnect
every expected silo pair and verify that each new connection authenticates,
baseline fallback and unexpected failure rates remain zero, and token-expiry
recycling succeeds for authenticated connections. Changing modes requires a
restart.

Before production, also verify that connections fail for an untrusted
certificate, wrong DNS SAN, wrong tenant or audience, missing role, unlisted
caller application ID, expired token, and a peer which supports only the
baseline Orleans ALPN. Repeat the checks after certificate and identity
rotation.

`Required` has no unauthenticated fallback. Roll back fleet-wide from
`Required` to `Audit`, and only then from `Audit` to `Disabled`. Never
automatically weaken the mode because Microsoft Entra or metadata is
unavailable.

The gateway validates external client bearer tokens using a distinct
`Orleans.Client.Connect` role and caller allowlist. External clients must call
`UseAuthenticatedClientConnections` with a token provider and the same exact
audience, tenant, client role, and cluster binding.

See the maintained [authenticated Orleans connections
guide](../../docs/site/src/content/docs/host/authenticated-silo-connections.md)
for the complete production setup, rollout, validation, monitoring, rotation,
and incident-response guidance.
