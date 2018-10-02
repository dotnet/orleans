---
layout: page
title: Timers and Reminders
---

# Timers and Reminders

The Orleans runtime provides two mechanisms, called timers and reminders, that enable the developer to specify periodic behavior for grains.

# Timers

## Timer Description

**Timers** are used to create periodic grain behavior that isn't required to span multiple activations (instantiations of the grain). It is essentially identical to the standard .**NET System.Threading.Timer** class.
In addition, it is subject to single threaded execution guarantees within the grain activation that it operates.

Each activation may have zero or more timers associated with it. The runtime executes each timer routine within the runtime context of the activation that it is associated with.

## Timer Usage

To start a timer, use the **Grain.RegisterTimer** method, which returns an  **IDisposable** reference:

``` csharp
public IDisposable RegisterTimer(
       Func<object, Task> asyncCallback, // function invoked when the timer ticks
       object state,                     // object tp pass to asyncCallback
       TimeSpan dueTime,                 // time to wait before the first timer tick
       TimeSpan period)                  // the period of the timer
```

Cancel the timer by disposing it.

A timer will cease to trigger if the activation is deactivated or when a fault occurs and its silo crashes.

Important Considerations

* When activation collection is enabled, the execution of a timer callback does not change the activation's state from idle to in use. This means that a timer cannot be used to postpone deactivation of otherwise idle activations.
* The period passed to **Grain.RegisterTimer** is the amount of time that passes from the moment the Task returned by **asyncCallback** is resolved to the moment that the next invocation of **asyncCallback** should occur. This not only makes it impossible for successive calls to **asyncCallback** to overlap but also makes it so that the length of time **asyncCallback** takes to complete affects the frequency at which **asyncCallback** is invoked. This is an important deviation from the semantics of **System.Threading.Timer**.
* Each invocation of **asyncCallback** is delivered to an activation on a separate turn and will never run concurrently with other turns on the same activation. Note however, **asyncCallback** invocations are not delivered as messages and are thus not subject to message interleaving semantics. This means that invocations of **asyncCallback** should be considered to behave as if running on a reentrant grain with respect to other messages to that grain.

# Reminders

## Reminder Description

Reminders are similar to timers with a few important differences:

* Reminders are persistent and will continue to trigger in almost all situations (including partial or full cluster restarts) unless explicitly cancelled.
* Reminder "definitions" are written to storage. However each specific occurrence, with its specific time, is not. This has the side effect that if the cluster is completely down at the time of a specific reminder tick, it will be missed and only the next tick of the reminder will happen.
* Reminders are associated with a grain, not any specific activation.
* If a grain has no activation associated with it and a reminder ticks, one will be created. e.g.: If an activation becomes idle and is deactivated, a reminder associated with the same grain will reactivate the grain when it ticks next.
* Reminders are delivered by message and are subject to the same interleaving semantics as all other grain methods.
* Reminders should not be used for high-frequency timers-- their period should be measured in minutes, hours, or days.

## Configuration

Reminders, being persistent, rely upon storage to function.
You must specify which storage backing to use before the reminder subsystem will function.
This is done by configuring one of the reminder providers via `UseXReminderService` extension methods, where X is the name of the provider, for example, `UseAzureTableReminderService`.

Azure Table configuration:

``` csharp
// TODO replace with your connection string
const string connectionString = "YOUR_CONNECTION_STRING_HERE";
var silo = new SiloHostBuilder()
    [...]
    .UseAzureTableReminderService(options => options.ConnectionString = connectionString)
    [...]
```

SQL:

``` csharp
// TODO replace with your connection string
const string connectionString = "YOUR_CONNECTION_STRING_HERE";
const string invariant = "YOUR_INVARIANT";
var silo = new SiloHostBuilder()
    [...]
    .UseAdoNetReminderService(options => 
    {
        options.ConnectionString = connectionString;
        options.Invariant = invariant;
    })
    [...]
```

 If you just want a placeholder implementation of reminders to work with without needing to set up an Azure account or SQL database, then this will give you a development-only implementation of the reminder system:

``` csharp
var silo = new SiloHostBuilder()
    [...]
    .UseInMemoryReminderService()
    [...]
```

## Reminder Usage

A grain that uses reminders must implement the **IRemindable.RecieveReminder** method.

``` csharp
Task IRemindable.ReceiveReminder(string reminderName, TickStatus status)
{
    Console.WriteLine("Thanks for reminding me-- I almost forgot!");
    return TaskDone.Done;
}
```

 To start a reminder, use the **Grain.RegisterOrUpdateReminder** method, which returns an **IOrleansReminder** object:

``` csharp
protected Task<IOrleansReminder> RegisterOrUpdateReminder(string reminderName, TimeSpan dueTime, TimeSpan period)
```

* reminderName is a string that must uniquely identify the reminder within the scope of the contextual grain.
* dueTime specifies a quantity of time to wait before issuing the first timer tick.
* period specifies the period of the timer.

Since reminders survive the lifetime of any single activation, they must be explicitly cancelled (as opposed to being disposed). You cancel a reminder by calling **Grain.UnregisterReminder**:

``` csharp
protected Task UnregisterReminder(IOrleansReminder reminder)
```

reminder is the handle object returned by **Grain.RegisterOrUpdateReminder**.

 Instances of **IOrleansReminder** aren't guaranteed to be valid beyond the lifespan of an activation. If you wish to identify a reminder in a way that persists, use a string containing the reminder's name.

 If you only have the reminder's name and need the corresponding instance of  **IOrleansReminder**, call the **Grain.GetReminder** method:

``` csharp
protected Task<IOrleansReminder> GetReminder(string reminderName)
```

## Which Should I Use?

We recommend that you use timers in the following circumstances:

* It doesn't matter (or is desirable) that the timer ceases to function if the activation is deactivated or failures occur.
* The resolution of the timer is small (e.g. reasonably expressible in seconds or minutes).
* The timer callback can be started from `Grain.OnActivateAsync` or when a grain method is invoked.

We recommend that you use reminders in the following circumstances:

* When the periodic behavior needs to survive the activation and any failures.
* To perform infrequent tasks (e.g. reasonably expressible in minutes, hours, or days).

## Combining Timers and Reminders

You might consider using a combination of reminders and timers to accomplish your goal.
For example, if you need a timer with a small resolution that needs to survive across activations, you can use a reminder that runs every five minutes whose purpose is to wake up a grain that restarts a local timer that may have been lost due to a deactivation.
