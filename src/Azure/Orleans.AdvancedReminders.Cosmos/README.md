# Microsoft Orleans Advanced Reminders for Azure Cosmos DB

## Introduction
Microsoft Orleans Advanced Reminders for Azure Cosmos DB provides persistence for Orleans advanced reminders using Azure Cosmos DB.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.AdvancedReminders.Cosmos
```

## Example - Configuring Azure Cosmos DB Advanced Reminders
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.AdvancedReminders;
using Orleans.Configuration;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            // Configure Azure Cosmos DB as reminder storage
            .UseCosmosAdvancedReminderService(options =>
            {
                options.ConfigureCosmosClient("AccountEndpoint=https://YOUR_COSMOS_ENDPOINT/;AccountKey=YOUR_COSMOS_KEY;");
                options.DatabaseName = "YOUR_DATABASE_NAME";
                options.ContainerName = "OrleansReminders";
                options.IsResourceCreationEnabled = true;
            });
    });

// Run the host
await builder.RunAsync();
```

## Example - Using Reminders in a Grain
```csharp
using Orleans;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Runtime;

public interface IReminderGrain
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
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Reminders and Timers](https://learn.microsoft.com/en-us/dotnet/orleans/grains/timers-and-reminders)
- [Reminder Services](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/reminder-services)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
