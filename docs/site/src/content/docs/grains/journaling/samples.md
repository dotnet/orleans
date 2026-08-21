---
title: Run Orleans Journaling samples
description: Run maintained Azure Blob and Redis examples for experimental Orleans Journaling.
ms.date: 08/21/2026
ms.topic: tutorial
---

# Run Orleans Journaling samples

The maintained [Journaling with Azure Blob JSON](https://github.com/dotnet/orleans/tree/main/samples/JournalingAzureBlobJson) sample exercises the experimental Journaling programming model end to end. It uses .NET Aspire and the Azure Storage emulator and runs with local emulator credentials.

The sample demonstrates:

- <xref:Orleans.Journaling.IDurableDictionary`2>, <xref:Orleans.Journaling.IDurableList`1>, <xref:Orleans.Journaling.IDurableQueue`1>, and <xref:Orleans.Journaling.IDurableSet`1>.
- <xref:Orleans.Journaling.IDurableValue`1>, journal-backed <xref:Orleans.Runtime.IPersistentState`1>, and <xref:Orleans.Journaling.IDurableTaskCompletionSource`1>.
- JSON source-generation metadata for all journaled payload types.
- Azure Blob WAL and checkpoint name customization.
- An acknowledged write, grain deactivation, reactivation, and recovered-state verification.
- Inspection of the raw JSON Lines journal.

## Run with Aspire

Install the .NET SDK, the [Aspire CLI](https://aspire.dev/get-started/install-cli/), and a Docker-compatible container runtime. From the sample directory, run:

```powershell
aspire run --project JournalingAzureBlobJson.AppHost
```

The app host starts the Azure Storage emulator and the sample service. The service writes every built-in durable state category, deactivates the grain, verifies the recovered values on a new activation, and prints the stored JSON Lines.

## Run the Redis sample

The [compiled Redis Journaling sample](https://github.com/dotnet/orleans/tree/main/docs/site/src/content/docs/grains/journaling/snippets/redis-journaling) configures Redis, writes an <xref:Orleans.Journaling.IDurableValue`1>, deactivates the grain, and verifies recovery on a new activation:

:::code language="csharp" source="./snippets/redis-journaling/Program.cs" id="redis_journal_counter":::

Start a local Redis server, then run the sample from the repository root:

```powershell
dotnet run --project docs/site/src/content/docs/grains/journaling/snippets/redis-journaling -- "localhost:6379"
```

Pass a StackExchange.Redis connection string as the first argument when Redis is available at another endpoint. The sample uses the isolated key prefix `journaling-docs-sample`. Configure Redis persistence and replication before evaluating server-restart recovery.
