---
layout: page
title: Debugging and Symbols
---

### Debugging

An Orleans-based application can be easily debugged during development by attaching a debugger to the silo host process or to the client process.
For fast development iterations, it is convenient to use a single process that combines a silos and a client, such as a console application project that gets created by the Orleans Dev/Test Host project template that is part of the [Microsoft Orleans Tools](https://marketplace.visualstudio.com/items?itemName=sbykov.MicrosoftOrleansToolsforVisualStudio) extension for Visual Studio.
Similarly, debugger can be attached to the Worker/Web Role instance process when running inside the Azure Compute Emulator.

In production, it is rarely a good idea to stop a silo at a breakpoint because the frozen silo will soon get voted dead by the cluster membership protocol and will not be able to communicate with other silos in the cluster.
Hence, in productions tracing is the primary 'debugging' mechanism.

### Source Link
Starting with the 2.0.0-beta1 release we added [Source Link](https://github.com/ctaggart/SourceLink) support to our Symbols. It means that if a project consumes the Orleans NuGet packages, when debugging the application code, they can step into the Orleans source code. In Steve Gordon's [blog post](https://www.stevejgordon.co.uk/debugging-asp-net-core-2-source), you can see what steps are needed to configure it.

### Symbols
Starting with 1.3.0 release, symbols for Orleans binaries are published to Microsoft symbol servers.
Make sure you enable `Microsoft Symbol Servers` in Visual Studio in Tools/Options/Debugging/Symbols for debugging Orleans code.

Prior to 1.3.0, symbols were published to [https://nuget.smbsrc.net/](https://nuget.smbsrc.net) symbol server.
Add it to the list of symbol servers in Visual Studio in Tools/Options/Debugging/Symbols.
Make sure there is a trailing slash in the URL.
Visual Studio 2015 has a bug with parsing it.

### Sources

You can download zipped sources for specific releases of Orleans from the [Releases page](https://github.com/dotnet/orleans/releases).
However, due to the LF/CR differences between Windows and Unix (default for GitHub), debugger may complain about a mismatch of the sources and symbols.
The workaround is to check out the corresponding version tag on a Windows machine to get sources with the matching LF/CR ending.
