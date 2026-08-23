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

## Aspire

AWS Aspire can run DynamoDB Local and provide CDK or CloudFormation table outputs to Orleans. See [Use Amazon DynamoDB with Aspire](https://dotnet.github.io/orleans/docs/host/dynamodb-aspire/) for clustering, grain storage, reminders, identity, and table lifecycle guidance.

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