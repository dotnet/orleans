namespace Grains

open System.Threading.Tasks
open HelloWorldInterfaces

type HelloGrain() = 
    inherit Orleans.Grain()
    interface IHelloGrain with
        override this.SayHello(greeting:string) =
            ValueTask.FromResult<string>("This comes from F#!")
