# Orleans transport layer security snippets

This folder contains the compile-checked snippet projects used by
`docs/site/src/content/docs/host/transport-layer-security.md`.

## Build the snippets

Run `dotnet build` for each project:

- `csharp/SiloExample/SiloExample.csproj`
- `csharp/ClientExample/ClientExample.csproj`

The entry points intentionally don't start a silo or client. The article examples
require deployment-specific certificates, names, and trust stores and are
designed to compile without embedding development-only certificate bypasses.

For a locally runnable mTLS example which creates a development certificate, see
the [Orleans Transport Layer Security sample](https://learn.microsoft.com/samples/dotnet/samples/orleans-transport-layer-security-tls/).
