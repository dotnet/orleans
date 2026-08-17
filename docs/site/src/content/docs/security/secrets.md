---
title: Secrets and credentials
description: Protect Orleans provider credentials, workload identities, certificates, and sensitive configuration.
ms.date: 08/17/2026
ms.topic: concept-article
---

# Secrets and credentials

Orleans supplies credentials from .NET configuration to provider-specific clients. The hosting platform's workload identity and secret-management facilities issue, store, and rotate those credentials. Apply the credential guidance for each clustering, storage, reminder, stream, and telemetry provider.

## Prefer workload identity

Use a workload identity or another short-lived credential mechanism when the provider supports it. For example, Azure providers commonly accept <xref:Azure.Core.TokenCredential>; production workloads can use <xref:Azure.Identity.ManagedIdentityCredential> for identity-based access. Follow the provider's documentation for equivalent mechanisms on other platforms.

Grant each workload the data-plane actions required by its role:

- Clients need access to gateway discovery data when the selected clustering provider requires it.
- Silos need membership operations and the specific storage, reminder, stream, and directory operations they use.
- Deployment and schema-management identities can be separate from runtime identities.
- Production, staging, and development should use separate identities and provider namespaces.

Treat <xref:Orleans.Configuration.ClusterOptions.ServiceId>, <xref:Orleans.Configuration.ClusterOptions.ClusterId>, grain keys, provider names, and connection endpoints as identifiers. Protect access through authenticated provider connections and network policy.

## Handle unavoidable secrets

When a provider requires a connection string, password, access key, or certificate password:

- Load it from an approved secret provider or protected deployment configuration.
- Restrict read access to the workload identity and to the operators responsible for rotation.
- Pass secrets through protected configuration channels designed to limit process inspection and diagnostic exposure.
- Redact credentials from logs, traces, metrics, exception messages, health responses, and dashboard-visible state.
- Keep grain keys and request context limited to non-secret identifiers and application metadata.

The [.NET configuration system](https://learn.microsoft.com/dotnet/core/extensions/configuration) combines multiple providers. Review the final precedence and give the production secret source the intended priority. Pass SDK credential objects through programmatic provider configuration.

## Protect certificates and private keys

TLS certificates are workload credentials. Limit private-key access to the process identity, use names and extended key usages appropriate to the workload role, and issue individual workload certificates when the deployment supports them.

Certificate loading, caching, disposal, refresh, and trust-store management are application and platform responsibilities. Plan an overlap window for issuer and leaf-certificate rotation, monitor expiry, and test reconnect behavior. See [load and rotate Orleans TLS certificates](../host/transport-layer-security.md#load-the-local-certificate).

## Design rotation and failure behavior

For every credential:

1. Record its owner, permissions, source, consumers, expiry, and rotation procedure.
1. Determine whether the provider SDK refreshes it automatically or the host must reconnect or restart.
1. Alert before expiry and on authentication failures while redacting the credential.
1. Test rotation while traffic is running and verify that old credentials are revoked after the overlap window.
1. Decide whether credential-provider failure should reject new work, preserve existing connections, or make the host unready.

Pair credential rotation with periodic authorization review. Verify that current provider identities retain least privilege and that revoked clients, silos, and operators are denied.
For network boundaries around credentials and provider connections, see [network hardening](networking.md).
