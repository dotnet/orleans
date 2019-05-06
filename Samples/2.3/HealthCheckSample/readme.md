# Orleans Health Checks Sample

Orleans Health Checks sample targeting .NET Core 2.1.

This sample demonstrates how to integrate [Microsoft.Extensions.Diagnostics.HealthChecks](https://github.com/aspnet/Extensions) into Orleans for custom health checks.

## Notes

* For simplicity, the solution contains a single silo project and no clients.
* The silo host is configured to use dynamic ports for silo communication, gateway and health checks. This is to facilitate multi-silo cluster testing on a single developer machine.

## Build & Run

On Windows, the easiest way to build and run the sample is to execute the `BuildAndRun.cmd` script.

Otherwise, use one of the following alternatives...

#### Bash

On bash-enabled platforms, run the `BuildAndRun.sh` bash script.

#### PowerShell

On powershell-enabled platforms, run the `BuildAndRun.ps1` PowerShell script.

#### Visual Studio

On Visual Studio, configure solution startup to start this project:

* Silo

You can start additional instances by right-clicking on the project and selecting *Debug -> Start new instance*.

#### dotnet

To build and run the sample step-by-step on the command line, use the following commands:

1. `dotnet restore` to restore NuGet packages.
2. `dotnet build --no-restore` to build the solution.
3. `start dotnet run --project ./src/Silo --no-build` to start the first silo.
3. `start dotnet run --project ./src/Silo --no-build` again to start additional silos.
