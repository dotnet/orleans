﻿namespace TestFSharp.AssemblyInfo

open System.Reflection
open System.Runtime.InteropServices

open Orleans.CodeGeneration

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[<assembly: AssemblyTitle("TestFSharp")>]

// Setting ComVisible to false makes the types in this assembly not visible 
// to COM components.  If you need to access a type in this assembly from 
// COM, set the ComVisible attribute to true on that type.
[<assembly: ComVisible(false)>]

// generate Orleans serializers for types in FSharp.core.dll
[<assembly: KnownAssembly(typedefof<unit option>)>]

do
    ()