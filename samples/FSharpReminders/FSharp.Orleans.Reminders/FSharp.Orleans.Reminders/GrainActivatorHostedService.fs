module FSharp.Orleans.Reminders.GrainActivatorHostedService

open System.Threading
open FSharp.Orleans.Reminders.Grains.IReminderGrain
open Microsoft.Extensions.Hosting
open Orleans


type GrainActivatorHostedService(client:IGrainFactory) =
    inherit BackgroundService()
    let _client=client

    override this.ExecuteAsync(cancellationToken:CancellationToken) = task{
        _client.GetGrain<IReminderGrain>("IReminderGrain")
                .WakeUp;
    }
