namespace UnitTests.FSharpInterfaces

open System.Threading.Tasks

type public DiscriminatedUnion<'T> =
    | Nothing
    | Something of 'T

type public IFSharpParameters<'T> =
    abstract OptionRoundtrip: 'T option -> Task<'T option>

