open Microsoft.Extensions.Logging
open Giraffe.Tasks
open Orleans
open Orleans.Runtime.Configuration
open Orleans.Hosting
open System
open FSharp.NetCore.Grains
open FSharp.NetCore.Interfaces

let buildSiloHost () =
      let config = ClusterConfiguration.LocalhostPrimarySilo()
      config.AddMemoryStorageProvider() |> ignore
      SiloHostBuilder()
        .UseConfiguration(config)
        .ConfigureApplicationParts(fun parts -> 
            parts.AddApplicationPart(typeof<HelloGrain>.Assembly)
                  .AddApplicationPart(typeof<IHello>.Assembly)
                  .WithCodeGeneration() |> ignore)
        .ConfigureLogging(fun logging -> logging.AddConsole() |> ignore)
        .Build()

[<EntryPoint>]
let main _ =
    let t = task {
        let host = buildSiloHost ()
        do! host.StartAsync ()

        printfn "Press any keys to terminate..."
        Console.Read() |> ignore

        do! host.StopAsync()

        printfn "SiloHost is stopped"
    }

    t.Wait()

    0
