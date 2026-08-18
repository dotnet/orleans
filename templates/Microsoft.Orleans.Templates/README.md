# Microsoft Orleans project templates

This package provides maintained templates for common Orleans application layouts.

Install the templates:

```dotnetcli
dotnet new install Microsoft.Orleans.Templates
```

Create a solution with separate grain contracts, grain implementations, silo, and external client projects:

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

Templates reference the current stable Orleans release. Pass `--orleans-version` when your application needs another Orleans version:

```dotnetcli
dotnet new orleans-web --name MyOrleansWebApp --orleans-version 10.2.2
```

The generated applications use localhost clustering for local development. Configure a shared clustering provider, durable storage, and production endpoints before deploying a multi-silo cluster. See the [Orleans hosting documentation](https://dotnet.github.io/orleans/docs/host/configuration-guide/) for production configuration guidance.
