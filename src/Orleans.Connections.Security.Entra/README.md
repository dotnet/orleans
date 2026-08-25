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

## Configure a v2 resource application

Keep token acquisition, JWT audience validation, and cluster authorization as
three separate values:

| Option | Example | Meaning |
|---|---|---|
| `TokenScope` | `api://11111111-1111-1111-1111-111111111111/contoso-prod-westus` | Cluster-qualified resource identifier used only to acquire a token. Orleans appends `/.default`. |
| `ResourceApplicationId` | `11111111-1111-1111-1111-111111111111` | Resource application's client-ID GUID, emitted as `aud` in an Entra v2 access token. |
| `ClusterRole` | `Orleans.Silo.Connect.contoso-prod-westus` | Exact, ordinal cluster authorization value. Use the corresponding `Orleans.Client.Connect.contoso-prod-westus` value for client connections. |

`TokenScope` is never validated as `aud`. A cluster-qualified identifier URI
can successfully acquire a token while the resulting v2 JWT contains the
resource application client-ID GUID as its audience.

```csharp
authentication.UseEntra(
    credential,
    entra =>
    {
        entra.Authority = new Uri(
            "https://login.microsoftonline.com/22222222-2222-2222-2222-222222222222/v2.0");
        entra.TokenScope =
            "api://11111111-1111-1111-1111-111111111111/contoso-prod-westus";
        entra.ResourceApplicationId =
            "11111111-1111-1111-1111-111111111111";
        entra.ValidTenantIds.Add(
            "22222222-2222-2222-2222-222222222222");
        entra.AllowedClientIds.Add(
            "33333333-3333-3333-3333-333333333333");
        entra.ClusterRole =
            "Orleans.Silo.Connect.contoso-prod-westus";
    });
```

The corresponding resource-application manifest uses the URI for token
requests, requests v2 access tokens, and defines an exact cluster role. Replace
the placeholders with identifiers, not secrets:

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
      "id": "<stable-cluster-role-guid>",
      "isEnabled": true,
      "value": "Orleans.Silo.Connect.contoso-prod-westus"
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

Assign the app role only to authorized workload service principals and also
configure the corresponding caller application-ID or service-principal
allowlist. When both allowlists are configured, matching either identity
authorizes the caller. Role assignment and allowlisting are independent checks.

As an alternative to `ClusterRole`, configure `ClusterClaimType` and have the
trusted issuer emit that signed custom claim with a value exactly equal to the
local Orleans cluster ID. Don't configure both mechanisms. A general caller
role in `RequiredRoles` doesn't replace this exact cluster binding.

## Enforcement and migration

`Required` rejects peers which don't negotiate authentication. `Audit` permits
unauthenticated baseline fallback only for a peer which doesn't negotiate the
authentication protocol. Once authentication is negotiated, acquisition,
validation, authorization, expiry, protocol, timeout, and overload failures
reject the connection in both modes.

`ClusterAudienceFormat` is obsolete and no longer authorizes a cluster. Migrate
by configuring the resource application's client-ID GUID in
`ResourceApplicationId`, retaining the cluster-qualified resource identifier
in `TokenScope`, and replacing the audience format with either an exact
`ClusterRole` or `ClusterClaimType`. Don't add the identifier URI to
`ValidAudiences` for an Entra v2 token. Advanced additional audiences, when
explicitly required, remain separate from the token request scope.
