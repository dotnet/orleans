# Authenticated silo connections

This sample configures mutual TLS (mTLS) and Microsoft Entra workload
authentication for silo-to-silo connections. It uses an explicit
`WorkloadIdentityCredential`; it doesn't construct `DefaultAzureCredential` or
copy JWT validation logic into the application.

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
3. Define the application role `Orleans.Silo.Connect` and allow applications
   as members.
4. Assign the role to each authorized silo workload identity.
5. Configure a federated identity credential for each workload and set its
   application ID in `AllowedCallerClientIds`.

The exact audience, tenant, application-token classification, caller
application ID, and application role are validated by
`Microsoft.Orleans.Connections.Security.Entra`. Don't replace that package with
sample-owned JWT parsing or validation.

## Configure TLS

Provide a PFX whose certificate has the Server Authentication and Client
Authentication EKUs and a DNS SAN matching `Certificate:TargetHost`. Install
the issuing private root in the operating-system trust store, then configure
its SHA-256 fingerprint. The sample requires successful platform chain,
validity, EKU, and revocation checks in both directions, requires the outbound
DNS-name check, and additionally pins the expected private root. Add the old
and new root fingerprints during CA rotation.

Supply the PFX password through a secret provider or the environment variable
`OrleansSecurity__Certificate__Password`; don't store it in `appsettings.json`.

## Run and observe

Use environment variables or a secret-aware configuration provider to replace
every placeholder in `appsettings.json`. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to
export the `Microsoft.Orleans` meter. Structured console logs preserve the
runtime's fixed event IDs and bounded authentication result categories.

Start in `Audit` mode. Proceed to `Required` only after every expected silo pair
has used the authentication protocol, baseline fallback and unexpected failure
rates remain zero for at least one maximum connection lifetime, and token
expiry recycling succeeds. Changing modes requires a restart.

`Required` has no unauthenticated fallback. Roll back fleet-wide from
`Required` to `Audit`, and only then from `Audit` to `Disabled`. Never
automatically weaken the mode because Microsoft Entra or metadata is
unavailable.

Client-to-gateway authentication is unchanged. Secure gateway traffic
separately with the existing TLS and application authentication mechanisms.
