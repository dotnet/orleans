# F# Orleans sample application with reminders

This is an example of how we can use Orleans reminders in F#

## Projects

We have 3 projects

### FSharp.Orleans.Reminders.Grains
This holds the grains and the interfaces. Right now there is only 1 grain, `ReminderGrain` and it's corresponding interface `IReminderGrain`

`IReminderGrain` also inherits from the `IRemindable` interface. This allows us to implement the `ReceiveReminder` function. This is the function that gets executed when the reminder gets triggered

```f#
member this.ReceiveReminder(reminderName:string, status:TickStatus) : Task =
    task{
        // Put your logic here
        return! Task.CompletedTask
    }
```

`OnActivateAsync` is where we set up the reminder. When it should start and the interval. Below I have set 60 seconds for both

```f#
override _.OnActivateAsync() = 
    let _periodTimeInSeconds = TimeSpan.FromSeconds 60
    let _dueTimeInSeconds = TimeSpan.FromSeconds 60
    let _reminder = base.RegisterOrUpdateReminder(base.GetPrimaryKeyString(), _dueTimeInSeconds, _periodTimeInSeconds)
    base.OnActivateAsync()
```

### FSharp.Orleans.Reminders.Codegen
This is responsible for generating grains that Orleans can use. This analyzes the `FSharp.Orleans.Reminders.Grains` project and emits the grains in C# 

### FSharp.Orleans.Reminders
This is the main project the sets up the silo and the dashboard. This is the project that runs the app.

## Dashboard

Browse to `localhost:9090` to view the dashboard

You can read the official docs on Timers & Reminders [here](https://learn.microsoft.com/en-us/dotnet/orleans/grains/timers-and-reminders).




