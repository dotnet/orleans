---
title: Core Orleans configuration options
description: Find the Orleans option types used for common hosting tasks.
ms.date: 08/02/2026
ms.topic: reference
---

# Core Orleans configuration options

Orleans options use the [.NET options pattern](https://learn.microsoft.com/dotnet/core/extensions/options). Configure them with <xref:Orleans.Hosting.SiloBuilderExtensions.Configure*> or <xref:Orleans.Hosting.ClientBuilderExtensions.Configure*>. Orleans also automatically binds the specific sections listed in [Declarative configuration](index.md#declarative-configuration); other option types require explicit binding.

This page is a curated starting point, not an exhaustive property catalog. <xref:Orleans.Configuration> and provider package APIs are the source of truth for the installed Orleans version.

## Common core options for client and silo builders

| Option type | Use it for |
|---|---|
| <xref:Orleans.Configuration.ClusterOptions> | <xref:Orleans.Configuration.ClusterOptions.ServiceId> and <xref:Orleans.Configuration.ClusterOptions.ClusterId> shared by silos and clients |
| <xref:Orleans.Configuration.NetworkingOptions> | Shared socket and connection settings |

<a id="iclientbuilder-specific-options"></a>

## <xref:Orleans.Hosting.IClientBuilder>-specific options

| Option type | Use it for |
|---|---|
| <xref:Orleans.Configuration.ClientMessagingOptions> | External client messaging and connections |
| <xref:Orleans.Configuration.GatewayOptions> | Client gateway refresh and preferred gateway behavior |

<a id="isilobuilder-specific-options"></a>

## <xref:Orleans.Hosting.ISiloBuilder>-specific options

| Option type | Use it for |
|---|---|
| <xref:Orleans.Configuration.SiloMessagingOptions> | Silo messaging, response timeouts, and connection behavior |
| <xref:Orleans.Configuration.EndpointOptions> | Advertised silo/gateway ports and listening endpoints |
| <xref:Orleans.Configuration.SiloOptions> | Silo name |
| <xref:Orleans.Configuration.ClusterMembershipOptions> | Membership probing, failure detection, and initial connectivity validation |
| <xref:Orleans.Configuration.GrainCollectionOptions> | Idle activation collection and memory-pressure shedding |
| <xref:Orleans.Configuration.GrainDirectoryOptions> | Built-in grain directory cache and partition behavior |
| <xref:Orleans.Configuration.LoadSheddingOptions> | Request rejection under host load |
| <xref:Orleans.Configuration.SchedulingOptions> | Grain scheduling limits and diagnostics |
| <xref:Orleans.Configuration.ProcessExitHandlingOptions> | Process-exit behavior |
| <xref:Orleans.Configuration.GrainTypeOptions> | Grain classes and interfaces supported by the process |

## Feature-specific options

Storage, clustering, reminders, streaming, serialization, dashboards, and third-party integrations define options in their own packages. Start with the provider's builder method, such as <xref:Microsoft.Extensions.Hosting.RedisClusteringISiloBuilderExtensions.UseRedisClustering*>, <xref:Orleans.Hosting.AzureBlobSiloBuilderExtensions.AddAzureBlobGrainStorage*>, or <xref:Orleans.Hosting.SiloBuilderReminderExtensions.UseAdoNetReminderService*>, then follow the linked options type in IntelliSense or API reference.

Named providers are normally configured using their builder methods:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="named_providers":::

Declarative named providers use `Orleans:{capability}:{name}` and a `ProviderType`; see [Declarative configuration](index.md#declarative-configuration).

## Find an option

1. Start from the hosting extension method for the feature.
2. Follow its options delegate type in IntelliSense.
3. Check the installed package's API reference for defaults and validation.
4. Inspect startup validation errors; Orleans validates required provider settings before the silo or client becomes ready.

Avoid copying all available properties into configuration. Leave defaults in place unless a deployment requirement or measurement justifies an override. This reduces version drift and makes intentional tuning visible.
