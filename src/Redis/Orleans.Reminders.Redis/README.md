# Microsoft Orleans Reminders for Redis

## Introduction
Microsoft Orleans Reminders for Redis provides persistence for Orleans reminders using Redis. This allows your Orleans applications to schedule persistent reminders that will be triggered even after silo restarts or grain deactivation.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Reminders.Redis
```

## Example - Configuring Redis Reminders
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            // Configure Redis as reminder storage
            .UseRedisReminderService(options =>
            {
                options.ConnectionString = "localhost:6379";
                options.Database = 0;
                options.KeyPrefix = "reminder-"; // Optional prefix for Redis keys
            });
    });

// Run the host
await builder.RunAsync();
```

## Example - Using Reminders in a Grain
```csharp
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
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Reminders and Timers](https://learn.microsoft.com/en-us/dotnet/orleans/grains/timers-and-reminders)
- [Reminder Services](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/reminder-services)
- [Redis Documentation](https://redis.io/documentation)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)