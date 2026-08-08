---
title: Secure Orleans connections with TLS
description: Configure server-authenticated TLS or mutual TLS for Orleans silo and client connections.
ms.date: 08/08/2026
ms.topic: how-to
---

# Secure Orleans connections with TLS

Orleans can protect client-to-silo and silo-to-silo connections with Transport Layer Security (TLS). TLS encrypts traffic and authenticates the endpoint acting as the TLS server. Mutual TLS (mTLS) additionally requires and authenticates the endpoint acting as the TLS client.

> [!IMPORTANT]
> <xref:Orleans.Connections.Security.TlsOptions.RemoteCertificateMode> defaults to `RequireCertificate`. On a silo's inbound connections, this default requires the connecting silo or Orleans client to present a certificate. To configure server-authenticated TLS without client certificates, explicitly set it to `NoCertificate` on every silo.

Install [Microsoft.Orleans.Connections.Security](https://www.nuget.org/packages/Microsoft.Orleans.Connections.Security) in every silo and client process.

## Choose an authentication model

| Model | Inbound silo policy | Outbound local-certificate policy | Typical boundary |
|---|---|---|---|
| Server-authenticated TLS | `RemoteCertificateMode = NoCertificate` | `ClientCertificateMode = NoCertificate` | Clients already authenticate at an application gateway or another trusted layer |
| mTLS | `RemoteCertificateMode = RequireCertificate` (the default) | `ClientCertificateMode = RequireCertificate` | Direct connections across a network where both workloads need cryptographic identity |

The two similarly named options apply at different stages:

- <xref:Orleans.Connections.Security.TlsOptions.RemoteCertificateMode> controls whether the remote endpoint must present a certificate. In server middleware, `RequireCertificate` requires a certificate from an inbound Orleans client or silo, `AllowCertificate` requests one but permits none, and `NoCertificate` doesn't request one. Silo configuration uses this same value for outbound middleware; the TLS server still presents a certificate and platform validation authenticates it when no custom callback is installed.
- <xref:Orleans.Connections.Security.TlsOptions.ClientCertificateMode> controls selection of the local client certificate in client middleware. On a silo, it applies when the silo initiates a silo-to-silo connection. On an Orleans client, it applies when the client initiates a gateway connection. It doesn't control inbound silo behavior.

`ClientCertificateMode` defaults to `AllowCertificate`: a configured local certificate is sent when it's valid for client authentication, but a missing or unsuitable local certificate is tolerated. Setting it to `RequireCertificate` makes the outbound requirement explicit and fails configuration or connection setup when an appropriate local certificate isn't available.

Every silo both accepts and initiates connections. For mTLS, a silo certificate therefore needs the Server Authentication extended key usage (EKU) for inbound connections and the Client Authentication EKU for outbound connections. For server-authenticated TLS, the silo certificate only needs Server Authentication. An Orleans client certificate used for mTLS needs Client Authentication. Certificate identity, issuance, and trust should reflect workload roles rather than reusing one certificate and private key across the cluster.

TLS provides confidentiality, integrity, and certificate-based peer
authentication for the Orleans transport. Orleans trusts a silo or client after
it is admitted; TLS doesn't provide per-grain authorization, isolate tenants, or
protect data after either process receives it. Configured membership and storage
providers and their data are trusted cluster infrastructure, and their transport
and access controls must be secured separately. Compromise of a trusted
certificate or private key can let an attacker enter the Orleans trust boundary
as that workload.

## Load the local certificate

The `UseTls` overloads accept an <xref:System.Security.Cryptography.X509Certificates.X509Certificate2> with an accessible private key. Load it from the certificate source supported by your deployment, and keep it undisposed for the lifetime of the Orleans host.

### Operating system certificate store

When the workload certificate is installed in an operating system certificate store, Orleans can load it by subject name. The following silo example searches the current user's Personal (`My`) store, requires the certificate to be currently valid, and configures server-authenticated TLS:

:::code language="csharp" source="./snippets/transport-layer-security/csharp/SiloExample/Program.cs" id="CertificateStore":::

Set `allowInvalid` to `false` outside isolated development environments. The store overload requires an accessible private key and selects a certificate suitable for the workload role. Ensure the selected certificate has every EKU required by the authentication model; in particular, a silo certificate used for mTLS needs both Server Authentication and Client Authentication.

Choose [CurrentUser](xref:System.Security.Cryptography.X509Certificates.StoreLocation) or [LocalMachine](xref:System.Security.Cryptography.X509Certificates.StoreLocation) according to the identity which runs the process, and grant that identity access to the private key. If a subject name can match more than one deployment certificate, load the intended certificate explicitly or use a certificate selector with an issuer, thumbprint, or other deployment-specific identity check.

### PKCS#12/PFX file

For a PKCS#12/PFX file, use <xref:System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile*>:

:::code language="csharp" source="./snippets/transport-layer-security/csharp/SiloExample/Program.cs" id="LoadPkcs12Certificate":::

Obtain the path and password from protected configuration or a secret provider rather than source code or ordinary configuration files. Restrict access to the file and its private key to the workload identity. Pass the returned certificate to the appropriate silo or client `UseTls` configuration shown in the following sections, keep it alive while the host runs, and dispose it after the host stops.

## Configure server-authenticated TLS

The silo presents a server certificate. Connecting clients and silos validate its chain, validity period, EKU, and DNS name but don't present a client certificate. The silo configuration explicitly disables remote certificates for inbound connections and local client certificates for outbound silo-to-silo connections.

:::code language="csharp" source="./snippets/transport-layer-security/csharp/SiloExample/Program.cs" id="ServerAuthenticatedTls":::

Configure an Orleans client without a local certificate:

:::code language="csharp" source="./snippets/transport-layer-security/csharp/ClientExample/Program.cs" id="ServerAuthenticatedTls":::

<xref:Orleans.Connections.Security.TlsClientAuthenticationOptions.TargetHost> must match a DNS Subject Alternative Name (SAN) on the server certificate. Use the stable service name clients use to reach the silos, not an arbitrary certificate subject.

## Configure mutual TLS

For mTLS, silos require a certificate from every inbound Orleans client or silo and require a local client certificate for every outbound silo-to-silo connection:

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

Set <xref:Orleans.Connections.Security.TlsOptions.CheckCertificateRevocation> to
check remote certificates on both inbound and outbound connections. Before
enabling it, verify that every workload can reach the certificate revocation
list (CRL) or Online Certificate Status Protocol (OCSP) service and decide how
outages should affect availability.

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
- For server-authenticated TLS, set `RemoteCertificateMode` and `ClientCertificateMode` to `NoCertificate` on silos.
- For mTLS, set `RemoteCertificateMode` and `ClientCertificateMode` to `RequireCertificate` on silos and configure a client-authentication certificate on every connecting Orleans client.
- Protect gateway and silo ports with network policy even when TLS is enabled.
- Keep clocks synchronized because certificate validity checks depend on time.
- Monitor TLS handshake failures and certificate expiration; don't log private keys or certificate passwords.
- Store PFX passwords in a secret store rather than source or ordinary configuration files.

## See also

- [Orleans security](../security/index.md)
- [Network hardening](../security/networking.md)
- <xref:Orleans.Connections.Security.TlsOptions>
- <xref:Orleans.Hosting.OrleansConnectionSecurityHostingExtensions.UseTls*>
- [Authenticate Orleans connections](authenticated-silo-connections.md)
- [Client configuration](configuration-guide/client-configuration.md)
- [Server configuration](configuration-guide/server-configuration.md)
- [.NET TLS/SSL best practices](https://learn.microsoft.com/dotnet/core/extensions/sslstream-best-practices)
- [Orleans Transport Layer Security (TLS) sample](https://learn.microsoft.com/samples/dotnet/samples/orleans-transport-layer-security-tls/)
