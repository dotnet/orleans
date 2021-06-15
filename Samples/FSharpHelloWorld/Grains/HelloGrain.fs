namespace Grains
open System.Threading.Tasks
open HelloWorldInterfaces

type HelloGrain() = 
    inherit Orleans.Grain()
    interface IHelloGrain with
        override this.SayHello(greeting:string) =
            Task.FromResult<string>("This comes from F#!")
