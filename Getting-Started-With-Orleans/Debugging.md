---
layout: page
title: Debugging and symbols
---
{% include JB/setup %}

An Orleans-based application can be easily debugged during development but simply attaching debugger to the silo host process, such as a host crteated with the Orleans Dev/Test Host project template, OrleansHost.exe, Azure Compute Emulator or any other host process.
In production, it is rearly a good idea to stop a silo at a breakpoint because the frozen silo will soon get voted dead by the cluster membership protocol and will not be able to communicate with other silos in the cluster.
Hence, in productions tracing is the primary 'debugging' mechanism.
 

## Symlbols
Symbols for Orleans binaries are published to https://nuget.smbsrc.net symbols server. Add it to the list of symbols server in the Visual Studio options under Debugging/Symbols for debugging Orleans code. Make sure there is traling slash in the URL. Visual Studio 2015 has a bug with parsing it.

## Sources

You can download zipped sources for specific releases of Orleans from the [Releases page](https://github.com/dotnet/orleans/releases).
