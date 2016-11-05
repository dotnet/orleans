---
layout: page
title: Code generation
---

# Code Generation

**Efficient code generation is one of the pillars of Orleans Runtime**. The Orleans Runtime makes use of generated code in order to ensure proper serialization of types that are used across the cluster as well as for generating boilerplate which abstracts away the implementation details of method shipping, exception propagation, and other internal runtime concepts.

There are two modes of `OrleansCodeGenerator`:

1.	**Build-time Codegen** - in this mode, codegen will run every time your project is compiled. An `MSBuild` task is injected in your project's build pipeline, and the code is generated in the project's `Properties\orleans.codegen.cs`. To activate this mode, add the package `Microsoft.Orleans.OrleansCodeGenerator.Build` to your **Grain Interface** project and build it. You will notice that when adding this package no extra references are added to your project but you will see the `Properties\orleans.codegen.cs` was added. Also if you edit your `.csproj` file, you will see that an extra `MSBuild` target was added as well. The good thing about using this mode is that you can step-into the generated code while debugging since the file is physically on the disk. However, your build time will be slower than usual if you have big projects. This is the default mode selected when you create a **Grain Interface** project by using the Visual Studio templates and it was the first codegen mode introduced since the release of Orleans. **Note**: To avoid the hassles of keeping this file under source control, we suggest you to to add it to your `.gitignore` or whatever ignore mechanism you have on your source control system, so it is not tracked by it, since this file will be re-generated every build.

2.	**Runtime Codegen** - introduced recently by @reubenbond, different from the build-time, this mode makes `OrleansCodeGenerator` to generate code when the Silo is starting. The good thing is that there is no `.cs` file on disk for the generated code, and you don't have an extra build step on your build pipeline. The bad thing, is that you can't step-into the generated code while debugging and it may increase your silo start time a bit. To enable this mode, if you were using the **build-time** codegen, remove all the references to `Microsoft.Orleans.OrleansCodeGenerator.Build` package and all the `Properties\orleans.codegen.cs` files then add a reference to `Microsoft.Orleans.OrleansCodeGenerator` in your **Silo host** project. If you created the project by using the Visual Studio templates, it should already be there otherwise, you can just install `icrosoft.Orleans.OrleansCodeGenerator` on your silo project. All set! The codegen will run at Silo startup.

Either way the code generated is exactly and there is just a trade-off on whether you want to debug the generated code and slow down your build time or, have faster builds without debug the generated code.