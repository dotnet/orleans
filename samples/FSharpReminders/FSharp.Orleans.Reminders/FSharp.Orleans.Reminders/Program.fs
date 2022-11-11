
open System
open FSharp.Orleans.Reminders.GrainActivatorHostedService
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans

open Orleans.ApplicationParts
open Orleans.Configuration
open Orleans.Hosting
open System.Net
open OrleansDashboard

[<EntryPoint>]
let main args =
    let theTask = task {
        
        let host = HostBuilder()
                       .UseOrleans(fun (builder : ISiloBuilder) ->
                            builder
                                .Configure(fun (clusterOptions:ClusterOptions) ->
                                    clusterOptions.ClusterId <- "fsharp-orleans-reminders-cluster-id"
                                    clusterOptions.ServiceId <- "fsharp-orleans-reminders-service-id"
                                )
                                .UseDashboard(fun (x : DashboardOptions) -> 
                                    x.HostSelf <- true
                                    x.Port <- 9090)
                                .UseLocalhostClustering()
                                .UseInMemoryReminderService()
                                .ConfigureEndpoints(IPAddress.Loopback, 2020, 4040)
                                .ConfigureServices(fun (serviceCollection:IServiceCollection) ->
                                    serviceCollection.AddHostedService<GrainActivatorHostedService>()                                    
                                    |> ignore
                                ).ConfigureApplicationParts (fun(applicationPartManager:IApplicationPartManager) ->
                                    applicationPartManager.ConfigureDefaults |> ignore
                                ) |> ignore
                            |> ignore
                        ).Build()               
                 
        do! host.StartAsync ()        
        printfn "App is now running"
        Console.ReadLine () |> ignore
    } 
    theTask.Wait()
    0
    