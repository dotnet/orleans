# Microsoft Orleans project templates

This package provides templates for common Orleans application layouts.

Install the templates:

```dotnetcli
dotnet new install Microsoft.Orleans.Templates
```

Create an Aspire solution with separate grain contracts, grain implementations, silo, external client, and AppHost projects:

```dotnetcli
dotnet new orleans --name MyOrleansApp
```

Create an ASP.NET Core app which co-hosts an Orleans silo and exposes a grain through an HTTP endpoint:

```dotnetcli
dotnet new orleans-web --name MyOrleansWebApp
```

Both templates target .NET 10 by default. Pass `--framework net8.0` to target .NET 8:

```dotnetcli
dotnet new orleans --name MyOrleansApp --framework net8.0
```

Templates reference the Orleans version shipped with the template package, including prerelease versions. Pass `--orleans-version` to select another Orleans version:

```dotnetcli
dotnet new orleans-web --name MyOrleansWebApp --orleans-version 10.2.2
```

The `orleans` template uses the Aspire Orleans integration to orchestrate the silo, client, and Azurite-backed Azure Table clustering and Azure Blob grain storage. Run the generated AppHost with a container runtime available so that Aspire can start Azurite. The `orleans-web` template uses localhost clustering for a one-node local development host. See the [Orleans hosting documentation](https://dotnet.github.io/orleans/docs/host/configuration-guide/) for production configuration guidance.
