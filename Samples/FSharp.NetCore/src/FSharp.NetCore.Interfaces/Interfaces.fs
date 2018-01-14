namespace FSharp.NetCore

module Interfaces =

    open System.Threading.Tasks

    type IHello = 
        inherit Orleans.IGrainWithIntegerKey
        abstract member SayHello : string -> Task<string>
