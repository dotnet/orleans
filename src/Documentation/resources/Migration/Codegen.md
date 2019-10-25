---
layout: page
title: Code Generation in Orleans 2.0
---

# Code Generation in Orleans 2.0

Code generation has been improved in Orleans 2.0, improving startup times and providing a more deterministic, debuggable experience. As with earlier versions, Orleans provides both build-time and run-time code generation.

1. **During Build** - This is the recommended option and only supports C# projects. In this mode, code generation will run every time your project is compiled. A build task is injected into your project's build pipeline and the code is generated in the project's intermediate output directory. To activate this mode, add one of the packages `Microsoft.Orleans.CodeGenerator.MSBuild` or `Microsoft.Orleans.OrleansCodeGenerator.Build` to all projects which contain Grains, Grain interfaces, serializers, or types which require serializers. Differences between the packages and additional codegen information could be found in the corresponding [Code Generation](../../grains/code_generation.md) section. Additional diagnostics can be emitted at build-time by specifying value for `OrleansCodeGenLogLevel` in the target project's *csproj* file. For example, `<OrleansCodeGenLogLevel>Trace</OrleansCodeGenLogLevel>`.

1. **During Configuration** - This is the only supported option for F#, Visual Basic, and other non-C# projects. This mode generates code during the configuration phase. To access this, see the Configuration documentation.

Both modes generate the same code, however run-time code generation can only generate code for publicly accessible types.