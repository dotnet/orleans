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

## Configuration via Microsoft.Extensions.Configuration

You can configure Orleans Redis reminders using `Microsoft.Extensions.Configuration` (such as `appsettings.json`) instead of configuring it in code. When using this approach, Orleans will automatically read the configuration from the `Orleans` section.

> **Note**: You can use either `"ProviderType": "Redis"` or `"ProviderType": "AzureRedisCache"` - both are supported and functionally equivalent.

### Example - appsettings.json
```json
{
  "ConnectionStrings": {
    "redis": "localhost:6379"
  },
  "Orleans": {
    "ClusterId": "my-cluster",
    "ServiceId": "MyOrleansService",
    "Reminders": {
      "ProviderType": "Redis",
      "ServiceKey": "redis",
      "Database": 0,
      "KeyPrefix": "reminder-"
    }
  }
}
```

### .NET Aspire Integration

For applications using .NET Aspire, consider using the [.NET Aspire Redis integration](https://learn.microsoft.com/en-us/dotnet/aspire/caching/stackexchange-redis-integration) which provides simplified Redis configuration, automatic service discovery, health checks, and telemetry. The Aspire integration automatically configures connection strings that Orleans can consume via the configuration system.

#### Example - Program.cs with Aspire Redis Integration
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Microsoft.Extensions.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);

// Add service defaults (Aspire configurations)
builder.AddServiceDefaults();

// Add Redis via Aspire client integration
builder.AddKeyedRedisClient("redis");

// Add Orleans
builder.UseOrleans();

var host = builder.Build();
await host.StartAsync();

// Get a reference to a grain and call it
var client = host.Services.GetRequiredService<IClusterClient>();
var grain = client.GetGrain<IReminderGrain>("user123");
await grain.StartReminder("AspireReminder");

Console.WriteLine("Reminder started with Aspire Redis!");
await host.WaitForShutdownAsync();
```

This example assumes your AppHost project has configured Redis like this:
```csharp
// In your AppHost/Program.cs
var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("redis");

var orleans = builder.AddOrleans("orleans")
    .WithReminders(redis);

builder.AddProject<Projects.MyOrleansApp>("orleans-app")
    .WithReference(orleans);

builder.Build().Run();
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