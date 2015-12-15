namespace Orleans.AssemblyInfo

open System.Reflection
open Orleans.CodeGeneration

[<assembly: AssemblyCompany("Microsoft Corporation")>]
[<assembly: AssemblyProduct("Orleans")>]
[<assembly: AssemblyCopyright("Copyright (c) Microsoft Corporation 2015")>]
[<assembly: AssemblyVersion("1.0.0.0")>]
[<assembly: AssemblyFileVersion("1.1.0.0")>]
[<assembly: AssemblyInformationalVersion("1.1.0.0")>]

// generate Orleans serializers for types in FSharp.core.dll
[<assembly: KnownAssembly(typedefof<unit option>)>]

do ()