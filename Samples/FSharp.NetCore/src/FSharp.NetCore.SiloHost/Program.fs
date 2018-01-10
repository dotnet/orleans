open Microsoft.Extensions.Logging
open Giraffe.Tasks
open Orleans
open Orleans.Runtime.Configuration
open Orleans.Hosting
open System
open FSharp.NetCore.Grians
open FSharp.NetCore.Interfaces


let buildSiloHost () =
    task {
        let config = ClusterConfiguration.LocalhostPrimarySilo()
        config.AddMemoryStorageProvider() |> ignore

        let builder = SiloHostBuilder()
                        .UseConfiguration(config)
                        .ConfigureApplicationParts(fun parts -> 
                            parts.AddApplicationPart(typeof<HelloGrain>.Assembly).WithCodeGeneration() |> ignore
                            parts.AddApplicationPart(typeof<IHello>.Assembly).WithCodeGeneration() |> ignore)
                        .ConfigureLogging(fun logging -> logging.AddConsole() |> ignore)
        return builder.Build()
    }

[<EntryPoint>]
let main _ =
    printfn "Hello World from F#!"
    let t = task {
        let! host = buildSiloHost ()
        do! host.StartAsync ()

        printfn "Press any keys to terminate..."
        Console.Read() |> ignore

        do! host.StopAsync()

        printfn "SiloHost is stopped"
    }
    t.Wait()

    0
