---
layout: page
title: Debugging and Symbols
---


An Orleans-based application can be easily debugged during development by simply attaching debugger to the silo host process, such as a host created with the Orleans Dev/Test Host project template, OrleansHost.exe, Azure Compute Emulator or any other host process.
In production, it is rarely a good idea to stop a silo at a breakpoint because the frozen silo will soon get voted dead by the cluster membership protocol and will not be able to communicate with other silos in the cluster.
Hence, in productions tracing is the primary 'debugging' mechanism.
 

## Symbols
Starting with 1.3.0 release, symbols for Orleans binaries are published to Microsoft symbol servers.
Make sure you enable `Microsoft Symbol Servers` in Visual Studio in Tools/Options/Debugging/Symbols for debugging Orleans code.

Prior to 1.3.0, symbols were published to [https://nuget.smbsrc.net/](https://nuget.smbsrc.net) symbol server.
Add it to the list of symbol servers in Visual Studio in Tools/Options/Debugging/Symbols.
Make sure there is a trailing slash in the URL.
Visual Studio 2015 has a bug with parsing it.

## Sources

You can download zipped sources for specific releases of Orleans from the [Releases page](https://github.com/dotnet/orleans/releases).
