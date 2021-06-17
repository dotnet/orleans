# Visual Basic Sample Application

This sample demonstrates how to use Visual Basic to write grain code. The sample consists of three projects:

* Interfaces - a Visual Basic project containing a grain interface, `IHelloGrain`
* Grains - a Visual Basic projects with a class, `HelloGrain`, which implements `IHelloGrain`
* HelloWorld - A C# project to host the grains

The `Microsoft.Orleans.CodeGenerator.MSBuild` package does not support emitting Visual Basic code, however it supports analyzing Visual Basic assemblies and emitting C# code.
Therefore, this sample works by instructing the code generator to analyze the `Grains` project when it is generating code for the `HelloWorld` project.
This is accomplished using the following directive in `HelloWorld`'s `Program.cs` file:

``` C#
[assembly: KnownAssembly(typeof(IHelloGrain))]
[assembly: KnownAssembly(typeof(HelloGrain))]
```

With the above attribute in place, the code generator analyzes the F# assembly and emits C# code into the `HelloWorld` project.

Run the sample by executing the following command:

``` powershell
dotnet run --project HelloWorld
```