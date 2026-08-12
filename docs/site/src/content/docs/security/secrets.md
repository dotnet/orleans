---
title: Secrets and credentials
description: Protect Orleans provider credentials, workload identities, certificates, and sensitive configuration.
ms.date: 08/12/2026
ms.topic: concept-article
---

# Secrets and credentials

Orleans consumes credentials through .NET configuration and provider-specific clients, but it doesn't store, issue, or rotate secrets. Use the hosting platform's workload identity and secret-management facilities, and apply the credential guidance for each clustering, storage, reminder, stream, and telemetry provider.

## Prefer workload identity

Use a workload identity or another short-lived credential mechanism when the provider supports it. For example, Azure providers commonly accept <xref:Azure.Core.TokenCredential>; production workloads can use <xref:Azure.Identity.ManagedIdentityCredential> instead of a storage account key. Follow the provider's documentation for equivalent mechanisms on other platforms.

Grant each workload only the data-plane actions it needs:

- Clients need access to gateway discovery data when the selected clustering provider requires it.
- Silos need membership operations and the specific storage, reminder, stream, and directory operations they use.
- Deployment and schema-management identities can be separate from runtime identities.
- Production, staging, and development should use separate identities and provider namespaces.

Don't treat <xref:Orleans.Configuration.ClusterOptions.ServiceId>, <xref:Orleans.Configuration.ClusterOptions.ClusterId>, grain keys, provider names, or connection endpoints as secrets.

## Handle unavoidable secrets

When a provider requires a connection string, password, access key, or certificate password:

- Load it from an approved secret provider or protected deployment configuration, not source code, sample files, container images, or committed manifests.
- Restrict read access to the workload identity and to the operators responsible for rotation.
- Avoid placing secrets in command-line arguments or other locations collected by process inspection and diagnostics.
- Don't write credentials to logs, traces, metrics, exception messages, health responses, or dashboard-visible state.
- Keep secret values out of grain keys and request context.

The [.NET configuration system](https://learn.microsoft.com/dotnet/core/extensions/configuration) combines multiple providers. Review the final precedence so a development setting can't override a production secret source unexpectedly. Prefer programmatic provider configuration when credentials are SDK objects rather than strings.

## Protect certificates and private keys

TLS certificates are workload credentials. Limit private-key access to the process identity, use names and extended key usages appropriate to the workload role, and avoid exporting one cluster-wide private key to every host when individual workload certificates are practical.

Certificate loading, caching, disposal, refresh, and trust-store management are application and platform responsibilities. Plan an overlap window for issuer and leaf-certificate rotation, monitor expiry, and test reconnect behavior. See [load and rotate Orleans TLS certificates](../host/transport-layer-security.md#load-the-local-certificate).

## Design rotation and failure behavior

For every credential:

1. Record its owner, permissions, source, consumers, expiry, and rotation procedure.
1. Determine whether the provider SDK refreshes it automatically or the host must reconnect or restart.
1. Alert before expiry and on authentication failures without logging the credential.
1. Test rotation while traffic is running and verify that old credentials are revoked after the overlap window.
1. Decide whether credential-provider failure should reject new work, preserve existing connections, or make the host unready.

Credential rotation doesn't replace authorization review. Periodically verify that provider identities still have least privilege and that removed clients, silos, and operators can no longer connect.
For network boundaries around credentials and provider connections, see [network hardening](networking.md).
For network boundaries around credentials and provider connections, see [network hardening](networking.md).
