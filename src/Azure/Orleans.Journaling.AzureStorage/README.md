# Microsoft Orleans Journaling for Azure Storage

## Introduction
Microsoft Orleans Journaling for Azure Storage provides an Azure Storage implementation of the Orleans Journaling provider. This allows logging and tracking of grain operations using Azure Storage as a backing store.

## Getting Started
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.Journaling.AzureStorage
```

## Example - Configuring Azure Storage Journaling
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Configuration;

var builder = Host.CreateApplicationBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            // Configure Azure Storage as a journaling provider
            .AddAzureTableJournal(
                name: "AzureJournal",
                configureOptions: options =>
                {
                    options.ConnectionString = "YOUR_AZURE_STORAGE_CONNECTION_STRING";
                    options.TableName = "GrainJournal";
                });
    });

// Run the host
await builder.RunConsoleAsync();
```

## Example - Using Journaling in a Grain
```csharp
public class JournaledGrain : JournaledGrain<MyState, MyEvent>, IJournaledGrain
{
    public Task<int> GetValue()
    {
        return Task.FromResult(State.Value);
    }

    public Task Increment()
    {
        // Record an event in the journal
        return RaiseEvent(new IncrementEvent());
    }

    public Task Add(int value)
    {
        // Record an event with a parameter
        return RaiseEvent(new AddEvent { AmountToAdd = value });
    }

    // Apply events to the state
    protected override void ApplyEvent(MyEvent @event)
    {
        switch (@event)
        {
            case IncrementEvent _:
                State.Value++;
                break;
            case AddEvent addEvent:
                State.Value += addEvent.AmountToAdd;
                break;
        }
    }
}

// State and event classes

public class MyState
{
    public int Value { get; set; }
}


public abstract class MyEvent
{
}


public class IncrementEvent : MyEvent
{
}


public class AddEvent : MyEvent
{
    public int AmountToAdd { get; set; }
}
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Orleans Journaling](https://learn.microsoft.com/en-us/dotnet/orleans/implementation/event-sourcing)
- [Event Sourcing Grains](https://learn.microsoft.com/en-us/dotnet/orleans/grains/event-sourcing)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)