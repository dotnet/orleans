---
layout: page
title: Code Generation
---

[!include[](../../warning-banner.md)]

# Code Generation

**Efficient code generation is one of the pillars of the Orleans Runtime**. The Orleans Runtime makes use of generated code in order to ensure proper serialization of types that are used across the cluster as well as for generating boilerplate which abstracts away the implementation details of method shipping, exception propagation, and other internal runtime concepts.

There are two modes of `OrleansCodeGenerator`:

1.	**Build-time Codegen** - in this mode, codegen will run every time your project is compiled. A build task is injected into your project's build pipeline and the code is generated in the project's intermediate output directory. To activate this mode, add the package `Microsoft.Orleans.OrleansCodeGenerator.Build` to your **Grain Interface** project. If you edit your `.csproj` file, you will see that an extra build target was added. This mode allows a user to step-into the generated code while debugging since the file is physically on the disk. However, your build time will be slower than usual if you have big projects. This is the default mode selected when you create a **Grain Interface** project by using the Visual Studio templates.

2.	**Runtime Codegen** - This mode makes `OrleansCodeGenerator` to generate code when the Silo is starting. As such, no code is generated during the build. Users can therefore not step-into the generated code while debugging and it will increase silo initialization time. To enable this mode, if you were using the **build-time** codegen, remove the the `Microsoft.Orleans.OrleansCodeGenerator.Build` package and install `Microsoft.Orleans.OrleansCodeGenerator` in your **Silo Host** and **Client** projects. If the project was created via the Visual Studio templates then it will already be installed. Otherwise, just install `Microsoft.Orleans.OrleansCodeGenerator` on your silo & client projects.

Both modes generate the same code with the exception that run-time code generation can only generate code for publicly accessible types.
