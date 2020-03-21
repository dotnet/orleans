namespace UnitTests.FSharpGrains

open System.Threading.Tasks
open UnitTests.GrainInterfaces
open Orleans

type Generic1ArgumentGrain<'T>() = 
    inherit Grain()

    interface INonGenericBase with 
        member x.Ping() = Task.CompletedTask

    interface IGeneric1Argument<'T> with 
        member x.Ping(t:'T) = Task.FromResult(t);
