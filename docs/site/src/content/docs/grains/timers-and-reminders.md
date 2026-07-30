---
title: Timers and reminders
description: Learn how to use timers and reminders in .NET Orleans.
ms.date: 01/20/2026
ms.topic: concept-article
ms.custom: sfi-ropc-nochange
zone_pivot_groups: orleans-version
---

# Timers and reminders

The Orleans runtime provides two mechanisms, timers and reminders, that enable you to specify periodic behavior for grains.

## Timers

Use **timers** to create periodic grain behavior that isn't required to span multiple activations (instantiations of the grain). A timer is identical to the standard .NET <xref:System.Threading.Timer?displayProperty=fullName> class. Additionally, timers are subject to single-threaded execution guarantees within the grain activation they operate on.

Each activation can have zero or more timers associated with it. The runtime executes each timer routine within the runtime context of its associated activation.

## Timer usage

:::zone target="docs" pivot="orleans-10-0,orleans-9-0,orleans-8-0"

To start a timer, use the <xref:Orleans.GrainBaseExtensions.RegisterGrainTimer*> method, which returns an <xref:Orleans.Runtime.IGrainTimer> reference:

```csharp
protected IGrainTimer RegisterGrainTimer<TState>(
    Func<TState, CancellationToken, Task> callback, // function invoked when the timer ticks
    TState state,                                   // object to pass to callback
    GrainTimerCreationOptions options)              // timer creation options
```

- `callback`: The callback function that receives the state and a <xref:System.Threading.CancellationToken> that is canceled when the timer is disposed or the grain deactivates.
- `state`: The state object passed to the callback.
- `options`: A <xref:Orleans.Runtime.GrainTimerCreationOptions> instance that configures timer behavior.

To cancel the timer, dispose of it.

A timer stops triggering if the grain deactivates or when a fault occurs and its silo crashes.

### GrainTimerCreationOptions

The <xref:Orleans.Runtime.GrainTimerCreationOptions> structure provides the following properties:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| <xref:Orleans.Runtime.GrainTimerCreationOptions.DueTime> | <xref:System.TimeSpan> | *Required* | The amount of time to delay before invoking the callback. Use `TimeSpan.Zero` to start immediately, or `Timeout.InfiniteTimeSpan` to prevent the timer from starting. |
| <xref:Orleans.Runtime.GrainTimerCreationOptions.Period> | <xref:System.TimeSpan> | *Required* | The time interval between invocations of the callback. Use `Timeout.InfiniteTimeSpan` to disable periodic signaling (one-shot timer). |
| <xref:Orleans.Runtime.GrainTimerCreationOptions.Interleave> | `bool` | `false` | When `true`, timer callbacks can interleave with other timers and grain calls. When `false`, callbacks are treated like grain calls and don't interleave (unless the grain is reentrant). |
| <xref:Orleans.Runtime.GrainTimerCreationOptions.KeepAlive> | `bool` | `false` | When `true`, timer callbacks extend the grain activation's lifetime, preventing idle collection. When `false`, timer callbacks don't prevent grain collection. |

### Example: Creating a grain timer

:::code language="csharp" source="./snippets/timers/TimerExamples.cs" id="grain_timer_example":::

***Important considerations:***

- When activation collection is enabled, executing a timer callback doesn't change the activation's state from idle to in-use by default. Set <xref:Orleans.Runtime.GrainTimerCreationOptions.KeepAlive> to `true` if you want timer callbacks to prevent deactivation.
- The period passed to <xref:Orleans.GrainBaseExtensions.RegisterGrainTimer*> is the amount of time passing from the moment the <xref:System.Threading.Tasks.Task> returned by `callback` resolves to the moment the next invocation of `callback` should occur. This not only prevents successive calls to `callback` from overlapping but also means the time `callback` takes to complete affects the frequency at which `callback` is invoked. This is an important deviation from the semantics of <xref:System.Threading.Timer?displayProperty=fullName>.
- Each invocation of `callback` is delivered to an activation on a separate turn and never runs concurrently with other turns on the same activation.
- Callbacks don't interleave by default. You can enable interleaving by setting <xref:Orleans.Runtime.GrainTimerCreationOptions.Interleave> to `true`.
- You can update grain timers using the <xref:Orleans.Runtime.IGrainTimer.Change*> method on the returned <xref:Orleans.Runtime.IGrainTimer> instance.
- Callbacks can keep the grain active, preventing collection if the timer period is relatively short. Enable this by setting <xref:Orleans.Runtime.GrainTimerCreationOptions.KeepAlive> to `true`.
- Callbacks can receive a <xref:System.Threading.CancellationToken> that is canceled when the timer is disposed or the grain starts to deactivate.
- Callbacks can dispose of the grain timer that fired them.
- Callbacks are subject to grain call filters.
- Callbacks are visible in distributed tracing when distributed tracing is enabled.
- POCO grains (grain classes that don't inherit from <xref:Orleans.Grain>) can register grain timers using the <xref:Orleans.GrainBaseExtensions.RegisterGrainTimer*> extension method.

:::zone-end

:::zone target="docs" pivot="orleans-7-0,orleans-3-x"

> [!IMPORTANT]
> The `RegisterTimer` API is obsolete starting in Orleans 8.2. If you're upgrading to Orleans 8.0 or later, migrate to the new <xref:Orleans.GrainBaseExtensions.RegisterGrainTimer*> API. See the [migration section](#migrate-from-registertimer-to-registergraintimer) for details.

To start a timer, use the `Grain.RegisterTimer` method, which returns an <xref:System.IDisposable> reference:

```csharp
protected IDisposable RegisterTimer(
    Func<object, Task> asyncCallback, // function invoked when the timer ticks
    object state,                     // object to pass to asyncCallback
    TimeSpan dueTime,                 // time to wait before the first timer tick
    TimeSpan period)                  // the period of the timer
```

To cancel the timer, dispose of it.

A timer stops triggering if the grain deactivates or when a fault occurs and its silo crashes.

***Important considerations:***

- When activation collection is enabled, executing a timer callback doesn't change the activation's state from idle to in-use. This means you can't use a timer to postpone the deactivation of otherwise idle activations.
- The period passed to `Grain.RegisterTimer` is the amount of time passing from the moment the <xref:System.Threading.Tasks.Task> returned by `asyncCallback` resolves to the moment the next invocation of `asyncCallback` should occur. This not only prevents successive calls to `asyncCallback` from overlapping but also means the time `asyncCallback` takes to complete affects the frequency at which `asyncCallback` is invoked. This is an important deviation from the semantics of <xref:System.Threading.Timer?displayProperty=fullName>.
- Each invocation of `asyncCallback` is delivered to an activation on a separate turn and never runs concurrently with other turns on the same activation.
- Timer callbacks can interleave with other grain calls and timers.

:::zone-end

## Migrate from RegisterTimer to RegisterGrainTimer

:::zone target="docs" pivot="orleans-10-0,orleans-9-0,orleans-8-0"

If you're upgrading from Orleans 7.x to Orleans 8.x or later, you should migrate from the obsolete `RegisterTimer` API to the new <xref:Orleans.GrainBaseExtensions.RegisterGrainTimer*> API.

### Key differences

| Aspect | `RegisterTimer` (Orleans 7.x) | <xref:Orleans.GrainBaseExtensions.RegisterGrainTimer*> (Orleans 8.x+) |
|--------|-------------------------------|-------------------------------------|
| **Interleaving** | Callbacks interleave by default | Callbacks do **not** interleave by default |
| **Return type** | <xref:System.IDisposable> | <xref:Orleans.Runtime.IGrainTimer> |
| **Callback signature** | `Func<object, Task>` | `Func<TState, CancellationToken, Task>` (receives <xref:System.Threading.CancellationToken>) |
| **State type** | `object` (untyped) | `TState` (strongly typed) |
| **CancellationToken** | Not supported | Supported via <xref:System.Threading.CancellationToken>, canceled on disposal or deactivation |
| **Updatable** | No | Yes, via <xref:Orleans.Runtime.IGrainTimer.Change*> method |
| **KeepAlive option** | Not supported | Supported via <xref:Orleans.Runtime.GrainTimerCreationOptions.KeepAlive>, prevents grain collection |
| **Call filters** | Not subject to filters | Subject to grain call filters |
| **Distributed tracing** | Not visible | Visible in distributed tracing |

### Migration example

**Before (Orleans 7.x):**

```csharp
public class MyGrain : Grain, IMyGrain
{
    private IDisposable? _timer;

    public override Task OnActivateAsync()
    {
        _timer = RegisterTimer(
            DoWorkAsync,
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10));

        return base.OnActivateAsync();
    }

    private Task DoWorkAsync(object state)
    {
        // Timer work - this interleaves with other calls
        return Task.CompletedTask;
    }
}
```

**After (Orleans 8.x+):**

:::code language="csharp" source="./snippets/timers/TimerExamples.cs" id="migrate_after":::

> [!WARNING]
> The default interleaving behavior changed in Orleans 8.2. The old `RegisterTimer` API allowed timer callbacks to interleave with other grain calls by default. The new <xref:Orleans.GrainBaseExtensions.RegisterGrainTimer*> API does **not** interleave by default. If your grain logic depends on interleaving behavior, set <xref:Orleans.Runtime.GrainTimerCreationOptions.Interleave> to `true` to preserve the old behavior.

:::zone-end

:::zone target="docs" pivot="orleans-7-0,orleans-3-x"

This section applies to Orleans 8.0 and later. Select the **Orleans 8.0**, **Orleans 9.0**, or **Orleans 10.0** pivot to view migration guidance.

:::zone-end

## Reminders

Reminders are similar to timers, with a few important differences:

- Reminders are persistent and continue to trigger in almost all situations (including partial or full cluster restarts) unless explicitly canceled.
- Reminder "definitions" are written to storage. However, each specific occurrence with its specific time isn't stored. This has the side effect that if the cluster is down when a specific reminder tick is due, it will be missed, and only the next tick of the reminder occurs.
- Reminders are associated with a grain, not any specific activation.
- If a grain has no activation associated with it when a reminder ticks, Orleans creates the grain activation. If an activation becomes idle and is deactivated, a reminder associated with the same grain reactivates the grain when it ticks next.
- Reminder delivery occurs via message and is subject to the same interleaving semantics as all other grain methods.
- You shouldn't use reminders for high-frequency timers; their period should be measured in minutes, hours, or days.

## Configuration

Since reminders are persistent, they rely on storage to function. You must specify which storage backing to use before the reminder subsystem can function. Do this by configuring one of the reminder providers via `Use{X}ReminderService` extension methods, where `X` is the name of the provider (for example, <xref:Orleans.Hosting.AzureStorageReminderSiloBuilderExtensions.UseAzureTableReminderService*>).

Azure Table configuration:

:::code language="csharp" source="./snippets/timers/ReminderConfiguration.cs" id="configure_azure_table":::

SQL:

:::code language="csharp" source="./snippets/timers/ReminderConfiguration.cs" id="configure_adonet":::

 If you just want a placeholder implementation of reminders to work without needing to set up an Azure account or SQL database, this provides a development-only implementation of the reminder system:

:::code language="csharp" source="./snippets/timers/ReminderConfiguration.cs" id="configure_inmemory":::

Redis:

:::code language="csharp" source="./snippets/timers/ReminderConfiguration.cs" id="configure_redis":::

The <xref:Orleans.Configuration.RedisReminderTableOptions> class provides the following configuration options:

| Property | Type | Description |
|----------|------|-------------|
| `ConfigurationOptions` | `ConfigurationOptions` | The StackExchange.Redis client configuration. Required. |
| `EntryExpiry` | `TimeSpan?` | Optional expiration time for reminder entries. Only set this for ephemeral environments like testing. Default is `null`. |
| `CreateMultiplexer` | `Func<RedisReminderTableOptions, Task<IConnectionMultiplexer>>` | Custom factory for creating the Redis connection multiplexer. |

Azure Cosmos DB:

Install the [Microsoft.Orleans.Reminders.Cosmos](https://www.nuget.org/packages/Microsoft.Orleans.Reminders.Cosmos) NuGet package and configure with `UseCosmosReminderService`:

:::code language="csharp" source="./snippets/timers/ReminderConfiguration.cs" id="configure_cosmos":::

The <xref:Orleans.Reminders.Cosmos.CosmosReminderTableOptions> class provides the following configuration options:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DatabaseName` | `string` | `"Orleans"` | The name of the Cosmos DB database. |
| `ContainerName` | `string` | `"OrleansReminders"` | The name of the container for reminder data. |
| `IsResourceCreationEnabled` | `bool` | `false` | When `true`, automatically creates the database and container if they don't exist. |
| `DatabaseThroughput` | `int?` | `null` | The provisioned throughput for the database. If `null`, uses serverless mode. |
| `ContainerThroughputProperties` | `ThroughputProperties?` | `null` | The throughput properties for the container. |
| `ClientOptions` | `CosmosClientOptions` | `new()` | The options passed to the Cosmos DB client. |

> [!IMPORTANT]
> If you have a heterogenous cluster, where the silos handle different grain types (implement different interfaces), every silo must add the configuration for Reminders, even if the silo itself doesn't handle any reminders.

### Aspire integration for reminders

:::zone target="docs" pivot="orleans-8-0,orleans-9-0,orleans-10-0"

When using [Aspire](../host/aspire-integration.md), you can configure Orleans reminders declaratively in your AppHost project. Aspire automatically injects the necessary configuration into your silo projects via environment variables.

#### Redis reminders with Aspire

**AppHost project (Program.cs):**

:::code language="csharp" source="../host/snippets/aspire/AppHost/AppHostExamples.cs" id="reminders_redis_apphost":::

**Silo project (Program.cs):**

:::code language="csharp" source="../host/snippets/aspire/Silo/SiloProgram.cs" id="reminders_redis_silo":::

#### Azure Table Storage reminders with Aspire

**AppHost project (Program.cs):**

:::code language="csharp" source="../host/snippets/aspire/AppHost/AppHostExamples.cs" id="reminders_azure_table_apphost":::

**Silo project (Program.cs):**

:::code language="csharp" source="../host/snippets/aspire/Silo/SiloProgram.cs" id="reminders_azure_table_silo":::

> [!TIP]
> During local development, Aspire automatically uses the Azurite emulator for Azure Storage. In production, configure a real Azure Storage account in your AppHost.

#### In-memory reminders for development with Aspire

For local development, you can use in-memory reminders that don't require external storage:

**AppHost project (Program.cs):**

:::code language="csharp" source="../host/snippets/aspire/AppHost/AppHostExamples.cs" id="reminders_inmemory_apphost":::

**Silo project (Program.cs):**

:::code language="csharp" source="../host/snippets/aspire/Silo/SiloProgram.cs" id="reminders_inmemory_silo":::

> [!WARNING]
> In-memory reminders are lost when the silo restarts. Only use `WithMemoryReminders()` for local development and testing. For production, always use a persistent reminder storage provider like Redis, Azure Table Storage, or SQL.

> [!IMPORTANT]
> You must call the appropriate `AddKeyed*` method (such as `AddKeyedRedisClient` or `AddKeyedAzureTableClient`) to register the backing resource in the dependency injection container. Orleans providers look up resources by their keyed service name—if you skip this step, Orleans won't be able to resolve the resource and will throw a dependency resolution error at runtime.

For more information about Orleans and Aspire integration, see [Orleans and Aspire integration](../host/aspire-integration.md).

:::zone-end

## Reminder usage

A grain using reminders must implement the <xref:Orleans.IRemindable.ReceiveReminder*?displayProperty=nameWithType> method.

```csharp
Task IRemindable.ReceiveReminder(string reminderName, TickStatus status)
{
    Console.WriteLine("Thanks for reminding me-- I almost forgot!");
    return Task.CompletedTask;
}
```

 To start a reminder, use the <xref:Orleans.Grain.RegisterOrUpdateReminder*?displayProperty=nameWithType> method, which returns an <xref:Orleans.Runtime.IGrainReminder> object:

```csharp
protected Task<IGrainReminder> RegisterOrUpdateReminder(
    string reminderName,
    TimeSpan dueTime,
    TimeSpan period)
```

- `reminderName`: is a string that must uniquely identify the reminder within the scope of the contextual grain.
- `dueTime`: specifies a quantity of time to wait before issuing the first-timer tick.
- `period`: specifies the period of the timer.

Since reminders survive the lifetime of any single activation, you must explicitly cancel them (as opposed to disposing of them). Cancel a reminder by calling <xref:Orleans.Grain.UnregisterReminder*?displayProperty=nameWithType>:

```csharp
protected Task UnregisterReminder(IGrainReminder reminder)
```

The `reminder` is the handle object returned by <xref:Orleans.Grain.RegisterOrUpdateReminder*?displayProperty=nameWithType>.

Instances of `IGrainReminder` aren't guaranteed to be valid beyond the lifespan of an activation. If you wish to identify a reminder persistently, use a string containing the reminder's name.

If you only have the reminder's name and need the corresponding `IGrainReminder` instance, call the <xref:Orleans.Grain.GetReminder*?displayProperty=nameWithType> method:

```csharp
protected Task<IGrainReminder> GetReminder(string reminderName)
```

## Decide which to use

We recommend using timers in the following circumstances:

- If it doesn't matter (or is desirable) that the timer stops functioning when the activation deactivates or failures occur.
- The timer's resolution is small (e.g., reasonably expressible in seconds or minutes).
- You can start the timer callback from <xref:Orleans.Grain.OnActivateAsync?displayProperty=nameWithType> or when a grain method is invoked.

We recommend using reminders in the following circumstances:

- When the periodic behavior needs to survive activation and any failures.
- Performing infrequent tasks (e.g., reasonably expressible in minutes, hours, or days).

## Combine timers and reminders

You might consider using a combination of reminders and timers to accomplish your goal. For example, if you need a timer with a small resolution that must survive across activations, you can use a reminder running every five minutes. Its purpose would be to wake up a grain that restarts a local timer possibly lost due to deactivation.

## POCO grain registrations

To register a timer or reminder with [a POCO grain](../migration-guide.md#poco-grains-and-igrainbase), implement the <xref:Orleans.IGrainBase> interface and inject <xref:Orleans.Timers.ITimerRegistry> or <xref:Orleans.Timers.IReminderRegistry> into the grain's constructor.

:::code source="./snippets/timers/PingGrain.cs":::

The preceding code does the following:

- Defines a POCO grain implementing <xref:Orleans.IGrainBase>, `IPingGrain`, and <xref:System.IDisposable>.
- Registers a timer invoked every 10 seconds, starting 3 seconds after registration.
- When `Ping` is called, registers a reminder invoked every hour, starting immediately after registration.
- The `Dispose` method cancels the reminder if it's registered.
