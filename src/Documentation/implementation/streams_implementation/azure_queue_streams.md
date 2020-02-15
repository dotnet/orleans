---
layout: page
title: Azure Queue Streams Implementation Details
---

# Orleans Azure Queue Streams Implementation Details

Each stream provider (Azure Queues, EventHub, SMS, SQS, ...) has it's own queue specific details and configuration.
This section provides some details about the usage, configuration and implementation of **Orleans Azure Queue Streams**.
More details are available in the streaming tests, which contain most of the configuration options, specifically [**`AQClientStreamTests`**](https://github.com/dotnet/orleans/tree/master/test/Extensions/TesterAzureUtils/Streaming/AQClientStreamTests.cs), [**`AQSubscriptionMultiplicityTests`**](https://github.com/dotnet/orleans/tree/master/test/Extensions/TesterAzureUtils/Streaming/AQSubscriptionMultiplicityTests.cs), and the extension functions for [**`IAzureQueueStreamConfigurator`**](https://github.com/dotnet/orleans/tree/master/src/Azure/Orleans.Streaming.AzureStorage/Providers/Streams/AzureQueue/AzureQueueStreamBuilder.cs)  and [**`ISiloPersistentStreamConfigurator`**](https://github.com/dotnet/orleans/tree/master/src/Orleans.Runtime.Abstractions/Streams/ISiloPersistentStreamConfigurator.cs).

## Overview

Orleans Azure Queue requires the package **Microsoft.Orleans.Streaming.AzureStorage** and the silo configuration at startup allow to specify the connection string

``` csharp
hostBuilder
  .AddAzureQueueStreams<AzureQueueDataAdapterV2>("AzureQueueProvider", optionsBuilder => optionsBuilder.Configure(options => {
    options.ConnectionString = "xxx";
  }))
  // also a PubSubStore is needed, as example using Azure Table Storage
  .AddAzureTableGrainStorage("PubSubStore", options => {
    options.ConnectionString = "xxx";
  })
```

The pulling agents will pull repeatedly until there are no more messages on a queue, then **delay** for a configurable period before continuing to pull. This happen for **each queue**.
Internally the pulling agents place messages in a **cache** (one cache per queue) for delivery to consumers, but will stop reading if the cache fills up. Messages are removed from the cache once consumers process the messages, so the read rate should roughly be throttled by the processing rate of the consumers.

By default it uses **8 queues** (see [**`AzureQueueOptions`**](https://github.com/dotnet/orleans/tree/master/src/Azure/Orleans.Streaming.AzureStorage/Providers/Streams/AzureQueue/AzureQueueStreamOptions.cs)) and 8 related pulling agents, a delay of **100ms** (see [**`StreamPullingAgentOptions.GetQueueMsgsTimerPeriod`**](https://github.com/dotnet/orleans/tree/master/src/Orleans.Core/Streams/PersistentStreams/Options/PersistentStreamProviderOptions.cs)) and a cache size (`IQueueCache`) of **4096 messages** (see [**`SimpleQueueCacheOptions.CacheSize`**](https://github.com/dotnet/orleans/tree/master/src/OrleansProviders/Streams/Common/SimpleCache/SimpleQueueCacheOptions.cs)).

## Configuration

The default configuration should fit a production environment, but for special needs it's possible to configure the default behaviour.
As example, in a development machine it's possible to reduce the number of the pulling agents to using just one queue.
This can help to reduce CPU usage and resource pressure.

``` csharp
hostBuilder
  .AddAzureQueueStreams<AzureQueueDataAdapterV2>("AzureQueueProvider", optionsBuilder => optionsBuilder.Configure(options => {
    options.ConnectionString = "xxx";
    options.QueueNames = new List<string> { "yourprefix-azurequeueprovider-0" };
  }))
```
