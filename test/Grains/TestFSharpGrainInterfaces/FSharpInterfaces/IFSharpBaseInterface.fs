namespace UnitTests.FSharpInterfaces

open System.Threading.Tasks

type public IFSharpBaseInterface =
    abstract Echo: int -> Task<int>
    abstract MultipleParameterEcho: string -> int -> Task<string*int>


