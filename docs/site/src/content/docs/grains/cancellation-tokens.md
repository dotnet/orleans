---
title: Cancel Orleans grain calls
description: Use CancellationToken for cooperative cancellation of Orleans grain calls.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Cancel Orleans grain calls

Orleans supports <xref:System.Threading.CancellationToken> parameters on grain methods. Cancellation is cooperative: Orleans delivers a cancellation signal, and the grain implementation decides where it is safe to stop.

## Add cancellation to a contract

Add at most one `CancellationToken` parameter. Put it last and make it optional when callers commonly don't need cancellation:

```csharp
public interface IImportGrain : IGrainWithGuidKey
{
    Task<int> Import(
        IReadOnlyList<string> records,
        CancellationToken cancellationToken = default);
}
```

Observe the token in the implementation and pass it to cancellation-aware APIs:

```csharp
public sealed class ImportGrain : Grain, IImportGrain
{
    public async Task<int> Import(
        IReadOnlyList<string> records,
        CancellationToken cancellationToken = default)
    {
        var imported = 0;

        foreach (string record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SaveRecord(record, cancellationToken);
            imported++;
        }

        return imported;
    }

    private static Task SaveRecord(
        string record,
        CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
}
```

If a token is already canceled before the call is sent, Orleans doesn't issue the request. If cancellation arrives while the request is queued, Orleans can remove it before execution. Once execution starts, the grain method must observe the token.

## Cancel a call

Pass a token from a <xref:System.Threading.CancellationTokenSource>:

```csharp
IImportGrain importer =
    grainFactory.GetGrain<IImportGrain>(Guid.NewGuid());

using var cancellation = new CancellationTokenSource(
    TimeSpan.FromSeconds(30));

try
{
    await importer.Import(records, cancellation.Token);
}
catch (OperationCanceledException)
    when (cancellation.IsCancellationRequested)
{
    // The caller requested cancellation.
}
```

Cancellation can cross client-to-grain and grain-to-grain calls. Delivery is best effort under network failure, and cancellation doesn't roll back side effects that completed before the grain observed the token.

## Timeouts and cancellation

A response timeout and cancellation answer different questions:

- A **timeout** stops the caller from waiting after no response arrives in time.
- **Cancellation** asks the callee to stop cooperatively.

By default, a timed-out call isn't canceled. <xref:Orleans.Configuration.MessagingOptions.CancelRequestOnTimeout> defaults to `false`. Enable it explicitly when timed-out operations should receive a cancellation signal:

```csharp
siloBuilder.Configure<SiloMessagingOptions>(options =>
{
    options.CancelRequestOnTimeout = true;
});

clientBuilder.Configure<ClientMessagingOptions>(options =>
{
    options.CancelRequestOnTimeout = true;
});
```

Even when enabled, the timeout doesn't prove that the operation stopped. The cancellation message can be delayed, the method might not observe its token, or side effects might already have completed.

`WaitForCancellationAcknowledgement` also defaults to `false`. Enabling it makes the caller wait for acknowledgement from the callee rather than completing local cancellation immediately. Use it only when that stronger coordination is worth the added latency and messaging.

## Design cancellable operations

- Check the token before expensive work and between independently safe units of work.
- Pass the token to I/O and other asynchronous dependencies.
- Leave state consistent at every cancellation point.
- Re-throw <xref:System.OperationCanceledException> after cleanup so the caller can distinguish cancellation from failure.
- Make externally visible operations idempotent when a caller might retry after an uncertain outcome.

Cancellation callbacks registered from grain code execute in the grain's scheduling context. Keep callbacks short and avoid blocking.

## Contract evolution

Orleans treats a `CancellationToken` specially in generated request contracts. Adding or removing a token parameter is wire-compatible with callers compiled against the other form: a missing token is represented as `CancellationToken.None`, and an extra token from an older caller can be ignored. Making a newly added parameter optional also preserves C# source compatibility for common call sites.

The older `GrainCancellationToken` API isn't needed for new applications. Use the standard .NET token.
