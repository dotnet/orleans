# Microsoft Orleans Reminders for DynamoDB

## Introduction
Microsoft Orleans Reminders for DynamoDB provides persistence for Orleans reminders using Amazon's DynamoDB. This allows your Orleans applications to schedule persistent reminders that will be triggered even after silo restarts or grain deactivation.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Reminders.DynamoDB
```

## Example - Configuring DynamoDB Reminders
```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            // Configure DynamoDB as reminder storage
            .UseDynamoDBReminderService(options =>
            {
                options.Service = "us-east-1";
                options.TableName = "OrleansReminders";
                options.UseProvisionedThroughput = false;
                options.CreateIfNotExists = false;
                options.UpdateIfExists = false;
            });
    });

// Run the host
var host = builder.Build();
await host.StartAsync();

// Get a reference to the grain
var reminderGrain = host.Services.GetRequiredService<IGrainFactory>()
    .GetGrain<IReminderGrain>("my-reminder-grain");

// Start the reminder
await reminderGrain.StartReminder("ExampleReminder");
Console.WriteLine("Reminder started!");

// Keep the host running until the application is shut down
await host.WaitForShutdownAsync();
```

`UseDynamoDBReminderService` configures reminder storage independently from the cluster membership provider. The AWS SDK credential and profile resolution chain supplies credentials when the reminder options omit explicit keys. This example uses on-demand capacity and an infrastructure-managed table.

## Strongly consistent schema migration

The provider defaults to the legacy schema. Legacy reads strongly point-validate GSI candidates and locally known omissions, and completed mutations send a bounded owner notification followed by a point read. This prevents stale resurrection and removal but cannot make a GSI omission discoverable for cold startup, newly acquired ranges, exhausted notifications, or arbitrary set reads. Legacy startup and refresh never use full-table scans.

V2 migration supplies the complete guarantee and is an explicit two-stage rollout:

1. Deploy every silo with `TableMode=Migrate` to create/backfill `${TableName}-v2` while retaining V1 reads and transactional dual writes.
2. After all silos are upgraded and migration reports `Ready`, deploy `TableMode=V2`. Cutover verifies the copies and fails if any active silo lacks a V2 compatibility marker.

V2 point, grain, and hash-range reads query the sharded base table with strong consistency. V1 remains transactionally maintained for rollback. After the rollback window, `TableMode=V2Only` performs an irreversible fenced transition before operators retire V1; the provider never deletes it. See [Configure Amazon DynamoDB reminders](https://dotnet.github.io/orleans/docs/grains/reminders/dynamodb/) for prerequisites, recovery, rollback, capacity, and compatibility details.

## Example - Using Reminders in a Grain
```csharp
using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace ReminderExample;

public interface IReminderGrain : IGrainWithStringKey
{
    Task StartReminder(string reminderName);
    Task StopReminder();
}

public class ReminderGrain : Grain, IReminderGrain, IRemindable
{
    private string _reminderName = "MyReminder";

    public async Task StartReminder(string reminderName)
    {
        _reminderName = reminderName;
        
        // Register a persistent reminder
        await RegisterOrUpdateReminder(
            reminderName,
            TimeSpan.FromMinutes(2),  // Time to delay before the first tick (must be > 1 minute)
            TimeSpan.FromMinutes(5)); // Period of the reminder (must be > 1 minute)
    }

    public async Task StopReminder()
    {
        // Find and unregister the reminder
        var reminder = await GetReminder(_reminderName);
        if (reminder != null)
        {
            await UnregisterReminder(reminder);
        }
    }

    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        // This method is called when the reminder ticks
        Console.WriteLine($"Reminder {reminderName} triggered at {DateTime.UtcNow}. Status: {status}");
        return Task.CompletedTask;
    }
}
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://dotnet.github.io/orleans/docs/)
- [Configure Amazon DynamoDB reminders](https://dotnet.github.io/orleans/docs/grains/reminders/dynamodb/)
- [Grain timers and reminders](https://dotnet.github.io/orleans/docs/grains/timers-and-reminders/)
- [AWS SDK for .NET Documentation](https://docs.aws.amazon.com/sdk-for-net/index.html)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)