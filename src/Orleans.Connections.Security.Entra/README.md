# Microsoft Orleans Entra silo connection authentication

This preview package integrates Microsoft Entra workload identities with authenticated Orleans
silo connections. Applications supply an `Azure.Core.TokenCredential`; the package does not create
credentials and does not depend on `Azure.Identity`.

The validator is fail-closed. Configure a tenant-specific HTTPS authority, exact audiences, tenant
identifiers, explicit caller authorization, and a cluster binding. Delegated tokens are rejected by
default. Metadata redirects and untrusted metadata hosts are rejected.

OpenID Connect metadata refresh is single-flight per configured authority. Unknown signing keys can
trigger at most one refresh per `UnknownSigningKeyRefreshInterval`. Failed refreshes use exponential
backoff with bounded jitter. Previously validated metadata remains usable during an outage for no
longer than `LastKnownGoodLifetime`; after that interval authentication fails. This intentionally
bounds how long a key removed by the authority can remain trusted during an outage.

The supplied credential remains responsible for token caching. Orleans requests a token for every
outbound authentication attempt and does not add another token cache.
