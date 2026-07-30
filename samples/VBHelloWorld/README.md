---
languages:
- vb
products:
- dotnet
- dotnet-orleans
page_type: sample
name: "Orleans Visual Basic sample app"
urlFragment: "orleans-vb-sample"
description: "An example of an Orleans sample app written in Visual Basic."
---

# Visual Basic sample app

This sample demonstrates how to use Visual Basic to write grain code. The sample consists of three projects:

* _HelloWorldInterfaces_: a C# project containing a grain interface, `IHelloGrain`.
* _Grains_: an Visual Basic projects implementing `IHelloGrain`.
* _HelloWorld_: A C# project to host the grains.

The `Microsoft.Orleans.Sdk` package does not support emitting Visual Basic code, however, it supports analyzing Visual Basic assemblies and emitting C# code. Therefore, this sample works by instructing the code generator to analyze the `Grains` project when it is generating code for the `HelloWorld` project. This is accomplished using the following directive in `HelloWorld`'s `Program.cs` file:

```csharp
[assembly: GenerateCodeForDeclaringAssembly(typeof(HelloGrain))]
[assembly: GenerateCodeForDeclaringAssembly(typeof(IHelloGrain))]
```

With the above attribute in place, the code generator analyzes the Visual Basic assembly and emits C# code into the `HelloWorld` project.

## Sample prerequisites

This sample is written in C# and Visual Basic and targets .NET 10. It requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later.

## Building the sample

To download and run the sample, follow these steps:

1. Download and unzip the sample.
2. In Visual Studio (2022 or later):
    1. On the menu bar, choose **File** > **Open** > **Project/Solution**.
    2. Navigate to the folder that holds the unzipped sample code, and open the C# project (.csproj) file.
    3. Choose the <kbd>F5</kbd> key to run with debugging, or <kbd>Ctrl</kbd>+<kbd>F5</kbd> keys to run the project without debugging.
3. From the command line:
   1. Navigate to the folder that holds the unzipped sample code.
   2. At the command line, type [`dotnet run`](https://docs.microsoft.com/dotnet/core/tools/dotnet-run).

## Running the sample

Run the sample by opening a terminal window and executing the following at the command prompt:

```dotnetcli
dotnet run --project HelloWorld
```
