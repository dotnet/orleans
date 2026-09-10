# Microsoft Orleans Advanced Reminders for DynamoDB

## Introduction
Microsoft Orleans Advanced Reminders for DynamoDB stores reminder definitions in Amazon DynamoDB.

This package does not include a DynamoDB-backed durable jobs implementation. You must also configure a durable jobs backend, for example `UseInMemoryDurableJobs()` for local development or `UseAzureBlobDurableJobs(...)` for persisted execution.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.AdvancedReminders.DynamoDB
```

## Example - Configuring DynamoDB Advanced Reminders
```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.DynamoDB;
using Orleans.Hosting;
using Orleans.DurableJobs;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            .UseInMemoryDurableJobs()
            // Configure DynamoDB for reminder definitions
            .UseDynamoDBAdvancedReminderService(options =>
            {
                options.AccessKey = "YOUR_AWS_ACCESS_KEY";
                options.SecretKey = "YOUR_AWS_SECRET_KEY";
                options.Service = "us-east-1";
                options.TableName = "OrleansAdvancedReminders";
                options.CreateIfNotExists = true;
                options.Capacity = DynamoDBCapacity.Provisioned(readUnits: 10, writeUnits: 5);
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

## Capacity policy

`Capacity` combines billing mode and its parameters in one immutable policy. The default remains `DynamoDBCapacity.Provisioned(readUnits: 10, writeUnits: 5)`. The read and write unit counts must both be positive and are applied to the reminder table and each global secondary index.

Select `DynamoDBCapacity.OnDemand` for pay-per-request billing. The named policy carries no provisioned capacity values. Table creation and updates still follow `CreateIfNotExists` and `UpdateIfExists`; infrastructure-managed tables can disable both operations.

Configuration binding accepts `"OnDemand"` or `"Provisioned(10,5)"` for `Capacity`. Connection strings use `Capacity=OnDemand` or `Capacity=Provisioned(10,5)`. Migrate `UseProvisionedThroughput=false` to `OnDemand`, or combine the former `ReadCapacityUnits` and `WriteCapacityUnits` values in `Provisioned(readUnits, writeUnits)`. Provider configuration and connection-string parsing reject the three old keys with a migration message.

## Example - Using Reminders in a Grain
```csharp
using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;

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
        await RegisterOrUpdateAdvancedReminder(
            reminderName,
            TimeSpan.FromMinutes(2),  // Time to delay before the first tick (must be > 1 minute)
            TimeSpan.FromMinutes(5)); // Period of the reminder (must be > 1 minute)
    }

    public async Task StopReminder()
    {
        // Find and unregister the reminder
        var reminder = await GetAdvancedReminder(_reminderName);
        if (reminder != null)
        {
            await UnregisterAdvancedReminder(reminder);
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
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Reminders and Timers](https://learn.microsoft.com/en-us/dotnet/orleans/grains/timers-and-reminders)
- [Reminder Services](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/reminder-services)
- [AWS SDK for .NET Documentation](https://docs.aws.amazon.com/sdk-for-net/index.html)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
