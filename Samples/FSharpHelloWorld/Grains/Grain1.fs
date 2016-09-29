namespace Grains

open System
open System.Threading.Tasks
open Orleans

open FSharpWorldInterfaces

type Grain1() = 
    inherit Orleans.Grain()

    interface IHello with

        override this.SayHello(greeting:string) =
            Task.FromResult<string>("This comes from F#!")
