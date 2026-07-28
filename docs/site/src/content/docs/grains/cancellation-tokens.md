---
title: Use cancellation tokens in Orleans grains
description: Learn how to implement cooperative cancellation in Orleans grain methods.
ms.date: 01/21/2026
zone_pivot_groups: orleans-version
---

# Use cancellation tokens in Orleans grains

:::zone target="docs" pivot="orleans-9-0,orleans-10-0,orleans-8-0,orleans-7-0,orleans-3-x"

Orleans supports cooperative cancellation in grain methods through the standard <xref:System.Threading.CancellationToken>. This feature lets you stop long-running operations early, cancel work that's no longer needed, and improve your application's responsiveness and resource utilization.

:::zone-end

## Overview

Orleans supports passing <xref:System.Threading.CancellationToken> instances to grain interface methods. This works for:

- Regular grain methods that return <xref:System.Threading.Tasks.Task>, <xref:System.Threading.Tasks.Task`1>, etcetera
- Streaming methods that return <xref:System.Collections.Generic.IAsyncEnumerable`1>
- Both client-to-grain and grain-to-grain calls

Cancellation is cooperative, meaning your grain implementation must observe the token and respond appropriately for it to be effective. If a cancellation token is not observed, the runtime will not automatically stop a method from executing. This behavior is consistent with the majority of libraries that support <xref:System.Threading.CancellationToken>. In other words, .NET uses cooperative cancellation. The major benefit of this approach is that cancellation can only occur at clearly identifiable points, not at any given instruction. This lets you run cleanup logic when cancellation is signalled.

Before a grain call is made, the runtime checks if the provided <xref:System.Threading.CancellationToken> is already canceled. If so, it immediately throws an <xref:System.OperationCanceledException> without issuing the request. Similarly, if an enqueued request is canceled before a grain begins executing it, it is cancelled without being executed.

While the grain call is executing, the runtime listens for cancellation, propagating the cancellation signal to the remote caller. Cancellation signals are only propagated while the call is active. Once the call completes (either normally or with an error), any subsequent cancellation signal will not be propagated to the remote caller even if it is still executing. These semantics are similar to how cancellation with remote calls works with other APIs such as <xref:System.Net.Http.HttpClient>, gRPC, or Azure API clients.

## Basic usage

Follow these steps to add cancellation support to your grain methods:

### 1. Define the grain interface

Add a <xref:System.Threading.CancellationToken> parameter as the last parameter in your grain interface method. Make it optional with a default value for better usability:

```csharp
public interface IProcessingGrain : IGrainWithGuidKey
{
    Task<string> ProcessDataAsync(string data, int chunks, CancellationToken cancellationToken = default);
}
```

### 2. Implement the grain method

In your grain implementation, regularly check the cancellation token during long-running operations:

```csharp
public class ProcessingGrain : Grain, IProcessingGrain
{
    public async Task<string> ProcessDataAsync(string data, int chunks, CancellationToken cancellationToken = default)
    {
        // Check cancellation before starting work
        cancellationToken.ThrowIfCancellationRequested();

        var results = new List<string>();

        for (int i = 0; i < chunks; i++)
        {
            // Check cancellation before each chunk
            cancellationToken.ThrowIfCancellationRequested();

            // Process each chunk
            var chunkResult = await ProcessChunkAsync(data, i);
            results.Add(chunkResult);

            // Use cancellation token with async operations when possible
            await Task.Delay(100, cancellationToken);
        }

        return string.Join(", ", results);
    }

    private async Task<string> ProcessChunkAsync(string data, int chunkIndex)
    {
        // Simulate processing work
        await Task.Delay(50);
        return $"{data}_chunk_{chunkIndex}";
    }
}
```

### 3. Call the grain with cancellation

Create a <xref:System.Threading.CancellationTokenSource> and pass its token to the grain method:

```csharp
var grain = grainFactory.GetGrain<IProcessingGrain>(Guid.NewGuid());

using var cts = new CancellationTokenSource();

// Set a timeout for automatic cancellation
cts.CancelAfter(TimeSpan.FromSeconds(30));

try
{
    var result = await grain.ProcessDataAsync("sample data", 20, cts.Token);
    Console.WriteLine($"Result: {result}");
}
catch (OperationCanceledException)
{
    Console.WriteLine("Operation was canceled");
}

// Manual cancellation example
var grain2 = grainFactory.GetGrain<IProcessingGrain>(Guid.NewGuid());
using var cts2 = new CancellationTokenSource();

// Start a long-running task
var task = grain2.ProcessDataAsync("large dataset", 1000, cts2.Token);

// Cancel after 5 seconds
await Task.Delay(5000);
cts2.Cancel();

try
{
    await task;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Long processing was canceled");
}
```

## Streaming with IAsyncEnumerable&lt;T&gt;

Cancellation is particularly useful for streaming scenarios where you might want to stop enumeration early. Orleans supports cancellation tokens in async enumerable grain methods.

```csharp
using System.Runtime.CompilerServices;

public interface IDataStreamGrain : IGrainWithGuidKey
{
    IAsyncEnumerable<DataPoint> StreamDataAsync(int count, CancellationToken cancellationToken = default);
}

public class DataStreamGrain : Grain, IDataStreamGrain
{
    public async IAsyncEnumerable<DataPoint> StreamDataAsync(
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < count; i++)
        {
            // Check cancellation before each yield
            cancellationToken.ThrowIfCancellationRequested();
            // Generate or fetch data
            var dataPoint = await GenerateDataPointAsync(i, cancellationToken);
            yield return dataPoint;

            // Optional: add delay with cancellation support
            await Task.Delay(100, cancellationToken);
        }
    }

    private async Task<DataPoint> GenerateDataPointAsync(int index, CancellationToken cancellationToken)
    {
        // Simulate data generation
        await Task.Delay(10, cancellationToken);
        return new DataPoint { Index = index, Value = Random.Shared.NextDouble() };
    }
}

public record DataPoint(int Index, double Value);
```

### Consuming the stream

When you consume async enumerables, you have two approaches for passing cancellation tokens: [Direct method call with cancellation](#approach-1-direct-method-call-with-cancellation) and [WithCancellation extension method](#approach-2-withcancellation-extension-method).

#### Approach 1: Direct method call with cancellation

When the async enumerable method has an <xref:System.Runtime.CompilerServices.EnumeratorCancellationAttribute> parameter, pass the token directly:

```csharp
var grain = grainFactory.GetGrain<IDataStreamGrain>(Guid.NewGuid());

using var cts = new CancellationTokenSource();
cts.CancelAfter(TimeSpan.FromSeconds(10)); // Auto-cancel after 10 seconds

try
{
    // The token is passed directly to the method and will be combined
    // with any token passed to GetAsyncEnumerator() internally
    await foreach (var dataPoint in grain.StreamDataAsync(1000, cts.Token))
    {
        Console.WriteLine($"Received: {dataPoint}");

        // Process the data point
        // Cancellation will stop the enumeration automatically
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Streaming was canceled");
}
```

#### Approach 2: WithCancellation extension method

For scenarios where you have an existing <xref:System.Collections.Generic.IAsyncEnumerable`1> instance or need to override the cancellation token, use the <xref:System.Threading.Tasks.TaskAsyncEnumerableExtensions.WithCancellation*> extension method:

```csharp
var grain = grainFactory.GetGrain<IDataStreamGrain>(Guid.NewGuid());
var asyncEnumerable = grain.StreamDataAsync(1000);

using var cts = new CancellationTokenSource();
cts.CancelAfter(TimeSpan.FromSeconds(10));

try
{
    // WithCancellation passes the token to GetAsyncEnumerator()
    await foreach (var dataPoint in asyncEnumerable.WithCancellation(cts.Token))
    {
        Console.WriteLine($"Received: {dataPoint}");
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Streaming was canceled");
}
```

## Backward compatibility

One of the key advantages of Orleans's cancellation token support is its backward compatibility. You can modify your grain interfaces and implementations to include or remove <xref:System.Threading.CancellationToken> parameters without breaking existing clients or other grains that call these methods.

- **Adding a <xref:System.Threading.CancellationToken>**: You can add a <xref:System.Threading.CancellationToken> parameter to an existing grain interface method. If an older client, compiled against the previous interface, calls this method without providing the token, the grain method will receive `CancellationToken.None` from the Orleans runtime. For C# source compatibility with existing grain *implementations* of the interface, it's recommended to define this new parameter as optional in the interface (for example, `CancellationToken cancellationToken = default`). New callers can provide a specific token.

- **Removing a <xref:System.Threading.CancellationToken>**: If you remove a <xref:System.Threading.CancellationToken> parameter from a grain method, callers that were previously passing a token will still have their calls succeed. The Orleans runtime will simply ignore the extra token parameter that the caller is sending, and the grain method will execute without it. The caller might still observe its own token's cancellation and could potentially time out or throw an `OperationCanceledException` on the calling side if its token is canceled, but the grain method itself won't receive the token.

This flexibility allows you to incrementally adopt cancellation in your Orleans applications without forcing a coordinated update across all components.

## Configuration

Configure cancellation behavior through <xref:Orleans.Configuration.SiloMessagingOptions> (for silos) and <xref:Orleans.Configuration.ClientMessagingOptions> (for clients):

```csharp
// In your silo configuration
siloBuilder.Configure<SiloMessagingOptions>(options =>
{
    // Send cancellation signal when requests timeout (default: true)
    options.CancelRequestOnTimeout = true;

    // Wait for callee to acknowledge cancellation (default: false)
    // Setting this to true provides stronger cancellation guarantees but may impact performance
    options.WaitForCancellationAcknowledgement = false;
});

// In your client configuration
clientBuilder.Configure<ClientMessagingOptions>(options =>
{
    options.CancelRequestOnTimeout = true;
    options.WaitForCancellationAcknowledgement = false;
});
```

### Configuration options explained

- **<xref:Orleans.Configuration.MessagingOptions.CancelRequestOnTimeout>**: When `true`, Orleans automatically sends a cancellation signal to the target grain if a request times out. This helps notify long-running operations that the caller is no longer waiting.

- **<xref:Orleans.Configuration.MessagingOptions.WaitForCancellationAcknowledgement>**: When `true`, the cancellation call waits for the target grain to acknowledge that it received the cancellation signal. This provides stronger guarantees but can impact performance in high-throughput scenarios.

## Diagnostics and troubleshooting

### Compiler diagnostics

Orleans code generation enforces that only one <xref:System.Threading.CancellationToken> parameter is allowed per grain method. If you add more than one, you'll get a build error with diagnostic ID `ORLEANS0109`:

> The type `YourGrainInterface` contains method `YourMethod` which has multiple CancellationToken parameters. Only a single CancellationToken parameter is supported.

**Incorrect:**

```csharp
public interface IMyGrain : IGrainWithGuidKey
{
    // This will cause ORLEANS0109 error
    Task ProcessAsync(CancellationToken token1, CancellationToken token2);
}
```

**Correct:**

```csharp
public interface IMyGrain : IGrainWithGuidKey
{
    Task ProcessAsync(CancellationToken cancellationToken = default);
}
```

### Monitoring cancellation activity

Orleans provides metrics to help you monitor cancellation behavior in your application:

- **`orleans-app-requests-canceled`**: Tracks the number of canceled requests.
- Monitor this metric to understand cancellation patterns and identify potential issues.

```csharp
// Example: Custom logging for cancellation tracking
public async Task MonitoredOperationAsync(CancellationToken cancellationToken = default)
{
    var stopwatch = Stopwatch.StartNew();

    try
    {
        await DoWorkAsync(cancellationToken);
        logger.LogInformation("Operation completed in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
    }
    catch (OperationCanceledException)
    {
        logger.LogInformation("Operation canceled after {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
        throw;
    }
}
```

## Performance considerations

### Cancellation batching

Orleans optimizes performance by batching cancellation requests when many cancellations occur simultaneously. This reduces network overhead and CPU load during high-cancellation scenarios, such as when a service shuts down or when many operations time out simultaneously.

### Callback execution context

Callbacks registered on a <xref:System.Threading.CancellationToken> within a grain execute on the grain's scheduler. This lets you safely perform operations in the context of the grain from cancellation callbacks.

```csharp
public async Task ExampleWithCancellationCallbackAsync(CancellationToken cancellationToken = default)
{
    // Register a callback that will run on the grain's scheduler
    cancellationToken.Register(() =>
    {
        // This runs on the grain's execution context
        // Safe to access grain state here
        logger.LogInformation("Operation was canceled for grain {GrainId}", this.GetPrimaryKey());
    });

    // Continue with work...
    await DoWorkAsync(cancellationToken);
}
```

## Behavioral notes and limitations

### Cooperative cancellation

Cancellation in Orleans is cooperative, which means:

- The grain method must actively check the <xref:System.Threading.CancellationToken> and respond to cancellation requests.
- Simply passing a canceled token doesn't automatically stop a running operation.
- For best results, check cancellation at regular intervals during long-running work.

### Timeout integration

When `CancelRequestOnTimeout` is enabled (default), Orleans automatically sends cancellation signals for timed-out requests:

```csharp
// If this call times out, Orleans sends a cancellation signal to the grain
await grain.LongRunningOperationAsync(cancellationToken);
```

### Cross-silo cancellation

Cancellation works across silo boundaries in distributed Orleans clusters:

- Client-to-grain calls can be canceled regardless of which silo hosts the grain.
- Grain-to-grain calls support cancellation even when grains are on different silos.
- Network partitions might delay cancellation signal delivery.

### Error handling patterns

Handle cancellation gracefully with proper error handling patterns:

```csharp
public async Task<ProcessingResult> ProcessWithFallbackAsync(
    string data,
    CancellationToken cancellationToken = default)
{
    try
    {
        return await ProcessPrimaryAsync(data, cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // Expected cancellation from our token - clean up and re-throw
        await CleanupAsync();
        throw;
    }
    catch (OperationCanceledException)
    {
        // Cancellation from a different token (unexpected) - treat as error
        logger.LogWarning("Received unexpected cancellation");
        throw;
    }
    catch (Exception ex)
    {
        // Unexpected error - try fallback if not canceled
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        logger.LogWarning(ex, "Primary processing failed, trying fallback");
        return await ProcessFallbackAsync(data, cancellationToken);
    }
}
```

When handling `OperationCanceledException`, always check if the cancellation is from the expected token using the `when` clause. This pattern distinguishes between expected cancellation (from your operation's token) and unexpected cancellation (from other sources).

### IAsyncEnumerable&lt;T&gt; behavior

For streaming scenarios with <xref:System.Collections.Generic.IAsyncEnumerable`1>:

- **Pre-enumeration cancellation**: If the token is canceled before enumeration starts, no items are yielded and an `OperationCanceledException` is thrown immediately.
- **Mid-enumeration cancellation**: If canceled during enumeration, the stream stops at the next cancellation check point (typically the next `yield` or `await` operation).
- **Exception propagation**: An `OperationCanceledException` is thrown to the consumer when cancellation occurs.
- **Resource cleanup**: The async enumerator's `DisposeAsync()` method is called automatically by `await foreach` to clean up resources.
- **Partial results**: Items yielded before cancellation are preserved and delivered to the consumer.

#### Cancellation timing considerations

The effectiveness of cancellation depends on how frequently the async iterator checks the cancellation token:

```csharp
public async IAsyncEnumerable<DataPoint> StreamDataAsync(
    int count,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    for (int i = 0; i < count; i++)
    {
        // Good: Check before each item
        cancellationToken.ThrowIfCancellationRequested();

        var data = await ProcessItemAsync(i);

        // Good: Check after long-running operations
        cancellationToken.ThrowIfCancellationRequested();

        yield return data;

        // Good: Use cancellation-aware operations
        await Task.Delay(100, cancellationToken);
    }
}
```

## Legacy: GrainCancellationToken and GrainCancellationTokenSource

Prior to the direct support for `System.Threading.CancellationToken` in grain methods, Orleans provided a specific mechanism using <xref:Orleans.GrainCancellationToken> and <xref:Orleans.GrainCancellationTokenSource>. **This approach is now considered legacy, and `System.Threading.CancellationToken` is the recommended method for implementing cancellation in Orleans grains.**

The following information is provided for context on older systems or for specific backward compatibility needs. For new development, see the <xref:System.Threading.CancellationToken> usage described earlier in this article.

### Overview of GrainCancellationToken

<xref:Orleans.GrainCancellationToken> is a wrapper around the standard <xref:System.Threading.CancellationToken>. It enables cooperative cancellation between threads, thread pool work items, or <xref:System.Threading.Tasks.Task> objects, and can be passed as a grain method argument.

A <xref:Orleans.GrainCancellationTokenSource> provides a <xref:Orleans.GrainCancellationToken> through its `Token` property and sends a cancellation message when its <xref:Orleans.GrainCancellationTokenSource.Cancel*?displayProperty=nameWithType> method is called.

### Using GrainCancellationToken

To use the legacy <xref:Orleans.GrainCancellationToken>:

1. Instantiate a <xref:Orleans.GrainCancellationTokenSource> object. This object manages and sends cancellation notifications to individual grain cancellation tokens.

    ```csharp
    var tcs = new GrainCancellationTokenSource();
    ```

1. Pass the token returned by the <xref:Orleans.GrainCancellationTokenSource.Token?displayProperty=nameWithType> property to each grain method that should listen for cancellation.

    ```csharp
    var waitTask = grain.LongIoWork(tcs.Token, TimeSpan.FromSeconds(10));
    ```

1. A cancellable grain operation needs to handle the underlying <xref:System.Threading.CancellationToken> property of <xref:Orleans.GrainCancellationToken> just like in any other .NET code.

    ```csharp
    public async Task LongIoWork(GrainCancellationToken tc, TimeSpan delay)
    {
        // Periodically check if cancellation has been requested
        while (!tc.CancellationToken.IsCancellationRequested)
        {
            // Perform a portion of the work
            await IoOperation(tc.CancellationToken);

            // Example: tc.CancellationToken.ThrowIfCancellationRequested();
        }
        // Perform cleanup if necessary and then exit or throw OperationCanceledException
    }
    ```

1. Call the `GrainCancellationTokenSource.Cancel()` method to initiate cancellation. This signals all <xref:Orleans.GrainCancellationToken> instances created from this source.

    ```csharp
    // Request cancellation
    await tcs.Cancel();
    ```

1. Call the `GrainCancellationTokenSource.Dispose()` method when finished with the <xref:Orleans.GrainCancellationTokenSource> object to release its resources.

    ```csharp
    tcs.Dispose();
    ```

#### Important considerations for GrainCancellationToken

- The `GrainCancellationTokenSource.Cancel()` method returns a <xref:System.Threading.Tasks.Task>. To ensure cancellation is robust against transient communication failures, you might need to retry the cancel call.
- Callbacks registered on the underlying `System.Threading.CancellationToken` (accessed via `GrainCancellationToken.CancellationToken`) are subject to single-threaded execution guarantees within the grain activation where they were registered. This means they will run on the grain's task scheduler.
- Each <xref:Orleans.GrainCancellationToken> can be passed through multiple method invocations if needed.

## Related topics

- <xref:System.Threading.CancellationToken>
- <xref:System.OperationCanceledException>
- <xref:System.Collections.Generic.IAsyncEnumerable`1>
- <xref:System.Runtime.CompilerServices.EnumeratorCancellationAttribute>
- <xref:Orleans.Configuration.SiloMessagingOptions>
- <xref:Orleans.Configuration.ClientMessagingOptions>
- [Cancellation in managed threads](../../standard/threading/cancellation-in-managed-threads.md)
- [Task cancellation](../../standard/parallel-programming/task-cancellation.md)
- [Async streams](/dotnet/csharp/language-reference/language-specification/statements#13953-await-foreach)
- [Orleans timers and reminders](timers-and-reminders.md)
- [Orleans streaming](../streaming/index.md)
- [Orleans GitHub repository](<https://github.com/dotnet/orleans>)
