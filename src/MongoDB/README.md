# MongoDB providers

The MongoDB providers are migrated from the MIT-licensed
[OrleansContrib/Orleans.Providers.MongoDB](https://github.com/OrleansContrib/Orleans.Providers.MongoDB)
at commit `2c8d7283c366e84a622a8b0b048a745522d9a9cc`, following
[OrleansContrib/Orleans.Providers.MongoDB#144](https://github.com/OrleansContrib/Orleans.Providers.MongoDB/issues/144).
The original copyright and license are preserved in [LICENSE](LICENSE) and
included in each provider package.

| Capability | Package |
|---|---|
| Shared MongoDB client configuration | Microsoft.Orleans.MongoDB |
| Membership and gateway discovery | Microsoft.Orleans.Clustering.MongoDB |
| Grain state persistence | Microsoft.Orleans.Persistence.MongoDB |
| Durable reminders | Microsoft.Orleans.Reminders.MongoDB |

Existing `Orleans.Providers.MongoDB.*` namespaces, collection names, document
fields, and serializer formats are retained. Replace the contributed package
reference with the capability packages used by the application and keep database
names, collection prefixes, membership strategy, grain keys, and serialization
configuration unchanged.

## Provider tests

Tests live in `test\Extensions\Orleans.MongoDB.Tests` and reuse the Orleans
clustering, persistence, and reminder contract suites. GitHub Actions runs them
against single-node MongoDB 6, 7, and 8 replica sets on both supported target frameworks,
and collects .NET 10 coverage through the shared provider test action.

For local runs, start a transaction-capable MongoDB replica set and set
`ORLEANSMONGODBCONNECTIONSTRING` to its connection string:

```powershell
$env:ORLEANSMONGODBCONNECTIONSTRING = "mongodb://localhost:27017/?replicaSet=orleans"
dotnet test --project test\Extensions\Orleans.MongoDB.Tests\Orleans.MongoDB.Tests.csproj --framework net10.0 --minimum-expected-tests 1
```

Each fixture owns an isolated database and removes it during disposal.
