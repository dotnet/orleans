---
title: Orleans and Aspire integration
description: Model and run Orleans applications with Aspire.
ms.date: 08/02/2026
ms.topic: how-to
---

# Orleans and Aspire integration

The `Aspire.Hosting.Orleans` package models an Orleans cluster and its backing services in an Aspire AppHost. [Aspire](https://aspire.dev/get-started/what-is-aspire/) supplies cluster identity, endpoints, provider configuration, service discovery, dependency ordering, and observability context to silo and client projects.

Use Aspire when you want a repeatable local distributed environment or already use an AppHost to describe deployment resources. Aspire orchestrates Orleans; it doesn't replace Orleans clustering, storage, reminder, or stream providers. See [Install the Aspire CLI](https://aspire.dev/get-started/install-cli/) for the supported toolchain.

## Add Orleans to the AppHost

Reference `Aspire.Hosting.Orleans` and the Aspire integrations for the resources you use:

:::code language="xml" source="snippets/aspire/AppHost/AppHost.csproj" id="apphost_packages":::

Define a clustering resource and an Orleans resource, then reference Orleans from the silo project:

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="basic_orleans_cluster":::

`.WithReplicas(3)` starts three local silo replicas. `.WaitFor(redis)` prevents the silo project from starting before Redis is ready.

Add only the capabilities the application needs:

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="orleans_with_storage_reminders":::

The named grain storage resources correspond to named Orleans providers such as `Default` and `PubSubStore`.

## Configure the silo project

Register the keyed Aspire client for every backing resource consumed by Orleans, then call parameterless `UseOrleans()`:

:::code language="csharp" source="snippets/aspire/Silo/SiloProgram.cs" id="silo_basic_config":::

The AppHost injects the `Orleans` configuration hierarchy. Orleans binds cluster identity, endpoints, clustering, reminders, streaming, grain storage, and grain directory configuration from it.

> [!IMPORTANT]
> Resource references inject configuration, but the application project must register the matching keyed service client. For example, use `AddKeyedRedisClient`, `AddKeyedAzureTableServiceClient`, or the matching Aspire integration method for the resource type and name.

Use the `UseOrleans` delegate only for configuration that the AppHost doesn't model, such as application-specific options or custom services.

## Configure a separate client

Create a client-only view of the Orleans resource with `.AsClient()`:

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="separate_silo_and_client":::

In the client project, register the keyed resource client and call parameterless `UseOrleansClient()`:

:::code language="csharp" source="snippets/aspire/Client/ClientProgram.cs" id="client_basic_config":::

The client receives the same cluster identity and clustering provider settings as the silos, but doesn't receive silo hosting capabilities.

## Use Azure resources

This compiled example uses Azurite for local Azure Storage development:

:::code language="csharp" source="snippets/aspire/AppHost/AppHostExamples.cs" id="azure_storage_aspire":::

Register the matching Azure Tables client in the silo:

:::code language="csharp" source="snippets/aspire/Silo/SiloProgram.cs" id="silo_azure_config":::

`.RunAsEmulator()` is a local-development choice. For production, bind the Azure Storage resource to a real account and configure identity and access in the deployment environment. Don't copy emulator configuration into a production AppHost.

The same principle applies to Redis and databases: the AppHost resource can launch a local container during development and bind to a managed service in deployment.

## Understand generated configuration

`AddOrleans` produces standard Orleans configuration. The application projects still call `UseOrleans` or `UseOrleansClient`, and Orleans validates the resulting provider configuration at startup. You can inspect injected environment variables in the Aspire dashboard when diagnosing a missing provider, keyed resource, or endpoint.

Common AppHost operations include:

| Operation | Purpose |
|---|---|
| `AddOrleans(name)` | Define an Orleans cluster resource. |
| `WithClustering(resource)` | Select the membership and gateway provider. |
| `WithGrainStorage(name, resource)` | Add named grain storage. |
| `WithReminders(resource)` | Add a durable reminder provider. |
| `WithStreaming(name, resource)` | Add a named stream provider. |
| `WithGrainDirectory(name, resource)` | Add a named grain directory. |
| `AsClient()` | Reference the cluster from a client-only project. |
| `WithReference(orleans)` | Inject Orleans configuration into a project. |

Consult the [Aspire Orleans integration reference](https://aspire.dev/integrations/frameworks/orleans/) for resource types and overloads supported by your Aspire version.

## Production considerations

- Treat the AppHost as a resource model, not as a substitute for durable services.
- Use managed identities or workload identities instead of embedding secrets.
- Keep `ServiceId` stable and isolate environments with `ClusterId`.
- Run multiple silo replicas across failure domains.
- Configure readiness, telemetry export, and graceful termination in each application project.
- Match keyed service names exactly between the AppHost and application projects.
