---
title: Secure Orleans connections with TLS
description: Configure server-authenticated TLS or mutual TLS for Orleans silo and client connections.
ms.date: 08/02/2026
ms.topic: how-to
---

# Secure Orleans connections with TLS

Orleans can protect client-to-silo and silo-to-silo connections with Transport Layer Security (TLS). TLS encrypts traffic and authenticates the endpoint acting as the TLS server. Mutual TLS (mTLS) additionally requires and authenticates the endpoint acting as the TLS client.

> [!IMPORTANT]
> Calling `UseTls` doesn't enable mTLS by itself. <xref:Orleans.Connections.Security.TlsOptions.ClientCertificateMode> defaults to `AllowCertificate`, so a client certificate is optional. Set it to <xref:Orleans.Connections.Security.RemoteCertificateMode.RequireCertificate> on every silo to require mTLS.

Install [Microsoft.Orleans.Connections.Security](https://www.nuget.org/packages/Microsoft.Orleans.Connections.Security) in every silo and client process.

## Choose an authentication model

| Model | What is authenticated | Silo setting | Typical boundary |
|---|---|---|---|
| Server-authenticated TLS | The endpoint accepting each connection | Default `ClientCertificateMode` (`AllowCertificate`) | Clients already authenticate at an application gateway or another trusted layer |
| mTLS | Both endpoints in each TLS connection | `ClientCertificateMode = RequireCertificate` | Direct connections across a network where both workloads need cryptographic identity |

Every silo both accepts and initiates connections. A silo certificate therefore commonly needs the Server Authentication extended key usage (EKU) for inbound connections and the Client Authentication EKU for outbound connections. An Orleans client certificate needs Client Authentication. Certificate identity, issuance, and trust should reflect workload roles rather than reusing one certificate and private key across the cluster.

TLS provides confidentiality, integrity, and certificate-based peer authentication for the Orleans transport. It doesn't authorize grain calls, isolate tenants, protect data after either process receives it, or secure membership/storage provider traffic unless those providers are separately configured. Compromise of a trusted certificate or private key can let an attacker impersonate that workload.

## Configure server-authenticated TLS

The silo presents a server certificate. Clients validate its chain, validity period, EKU, and DNS name but don't need to present a certificate.

:::code language="csharp" source="./snippets/transport-layer-security/csharp/SiloExample/Program.cs" id="ServerAuthenticatedTls":::

Configure an Orleans client without a local certificate:

:::code language="csharp" source="./snippets/transport-layer-security/csharp/ClientExample/Program.cs" id="ServerAuthenticatedTls":::

`TargetHost` must match a DNS Subject Alternative Name (SAN) on the server certificate. Use the stable service name clients use to reach the silos, not an arbitrary certificate subject.

## Configure mutual TLS

For mTLS, silos require a client certificate and clients provide one:

:::code language="csharp" source="./snippets/transport-layer-security/csharp/SiloExample/Program.cs" id="MutualTls":::

:::code language="csharp" source="./snippets/transport-layer-security/csharp/ClientExample/Program.cs" id="MutualTls":::

The default platform validation applies when <xref:Orleans.Connections.Security.TlsOptions.RemoteCertificateValidation> isn't set. If you provide that callback, keep normal chain and name checks and add only the deployment-specific policy you require. A callback which returns `true` unconditionally defeats peer authentication.

> [!WARNING]
> <xref:Orleans.Connections.Security.TlsOptions.AllowAnyRemoteCertificate> is suitable only for isolated local development. It accepts any remote certificate and therefore doesn't protect against impersonation or an active man-in-the-middle.

## Establish certificate trust

Treat these as separate trust decisions:

- **Clients trust silos:** The issuing CA for silo server certificates is trusted by Orleans clients. The certificate SAN matches `TargetHost`.
- **Silos trust clients:** For mTLS, the issuing CA for client certificates is trusted by every silo. Use a private CA or an additional validation policy when possession of an arbitrary public certificate isn't sufficient authorization.
- **Silos trust silos:** Each silo validates the server certificate on outbound silo-to-silo connections. With mTLS, each silo also presents a client-authentication certificate.

Keep trust stores narrow. Don't place unrelated public or corporate roots in a workload-specific trust bundle when any certificate from those roots would be accepted as a cluster identity. Network policy should still restrict silo and gateway ports to expected peers.

## Protocols and revocation

<xref:Orleans.Connections.Security.TlsOptions.SslProtocols> defaults to TLS 1.2 and TLS 1.3. Retain those defaults unless an interoperability or policy requirement calls for a narrower set. Orleans doesn't enable TLS 1.0 or TLS 1.1 by default.

Set <xref:Orleans.Connections.Security.TlsOptions.CheckCertificateRevocation> according to your public key infrastructure (PKI). Before enabling it, verify that every workload can reach the certificate revocation list (CRL) or Online Certificate Status Protocol (OCSP) service and decide how outages should affect availability.

## Rotate certificates

Plan rotation before deployment:

1. Issue the replacement certificate with the same required names and EKUs.
2. Distribute the new issuing chain to trust stores before any endpoint presents the new certificate.
3. Make both old and new chains valid during an overlap window.
4. Restart processes with the replacement certificate, or use <xref:Orleans.Connections.Security.TlsOptions.LocalServerCertificateSelector> and <xref:Orleans.Connections.Security.TlsOptions.LocalClientCertificateSelector> to select certificates dynamically.
5. Confirm new connections use the replacement, then remove the old certificate and obsolete trust roots.

Certificate selectors are called during authentication, but certificate loading, caching, disposal, and refresh are application responsibilities. Test rotation under normal reconnect and silo restart behavior. Alert on certificate expiration well before the overlap window closes.

## Production checklist

- Give private-key files or key-store entries only to the workload identity that needs them.
- Prefer separate certificates per workload or instance over one exported cluster-wide private key.
- Validate SANs, EKUs, chain trust, validity, and revocation behavior.
- Set `ClientCertificateMode` to `RequireCertificate` everywhere mTLS is required.
- Protect gateway and silo ports with network policy even when TLS is enabled.
- Keep clocks synchronized because certificate validity checks depend on time.
- Monitor TLS handshake failures and certificate expiration; don't log private keys or certificate passwords.
- Store PFX passwords in a secret store rather than source or ordinary configuration files.

## See also

- <xref:Orleans.Connections.Security.TlsOptions>
- <xref:Orleans.Hosting.OrleansConnectionSecurityHostingExtensions.UseTls*>
- [.NET TLS/SSL best practices](../../core/extensions/sslstream-best-practices.md)
