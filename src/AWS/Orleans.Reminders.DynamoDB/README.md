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
using Orleans.Configuration;
using Orleans.Hosting;

var builder = new HostBuilder()
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            // Configure DynamoDB as reminder storage
            .UseDynamoDBReminderService(options =>
            {
                options.AccessKey = "YOUR_AWS_ACCESS_KEY";
                options.SecretKey = "YOUR_AWS_SECRET_KEY";
                options.Region = "us-east-1";
                options.TableName = "OrleansReminders";
                options.CreateIfNotExists = true;
            });
    });

// Run the host
await builder.RunConsoleAsync();
```

## Example - Using Reminders in a Grain
```csharp
using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

public class ReminderGrain : Grain, IReminderGrain, IRemindable
{
    private IDisposable _timer;
    private string _reminderName = "MyReminder";

    public async Task StartReminder(string reminderName)
    {
        _reminderName = reminderName;
        
        // Register a persistent reminder
        await RegisterOrUpdateReminder(
            reminderName,
            TimeSpan.FromSeconds(5),  // Time to delay before the first tick
            TimeSpan.FromSeconds(30)); // Period of the reminder
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