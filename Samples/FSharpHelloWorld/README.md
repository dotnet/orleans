# F# Sample Application

This sample demonstrates how to use F# to write grain code. The sample consists of three projects:

* HelloWorldInterfaces - a C# project containing a grain interface, `IHelloGrain`
* Grains - an F# projects implementing `IHelloGrain`
* HelloWorld - A C# project to host the grains

The `Microsoft.Orleans.CodeGenerator.MSBuild` package does not support emitting F# code, however it supports analyzing F# assemblies and emitting C# code. Therefore, this sample works by instructing the code generator to analyze the `Grains` project when it is generating code for the `HelloWorld` project. This is accomplished using the following directive in `HelloWorld`'s `Program.cs` file:

``` C#
[assembly: KnownAssembly(typeof(Grains.HelloGrain))]
```

With the above attribute in place, the code generator analyzes the F# assembly and emits C# code into the `HelloWorld` project.

Run the sample by executing the following command:

``` powershell
dotnet run --project HelloWorld
```