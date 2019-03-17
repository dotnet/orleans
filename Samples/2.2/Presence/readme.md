# Presence Service

Orleans Presence Service sample targeting .NET Core 2.1.

## Build & Run

On Windows, the easiest way to build and run the sample is to execute the `BuildAndRun.ps1` PowerShell script.

Otherwise, use one of the following alternatives...

#### Bash

On bash-enabled platforms, run the `BuildAndRun.sh` bash script.

#### Visual Studio 2017

On Visual Studio 2017, configure solution startup to start these projects at the same time:

* Silo
* PlayerWatcher
* LoadGenerator

#### dotnet

To build and run the sample step-by-step on the command line, use the following commands:

1. `dotnet restore` to restore NuGet packages.
2. `dotnet build --no-restore` to build the solution.
3. `dotnet run --project ./src/Silo --no-build` on a separate window to start the silo host.
4. `dotnet run --project ./src/PlayerWatcher --no-build` on a separate window to start the player watcher.
5. `dotnet run --project ./src/LoadGenerator --no-build` on a separate window to start the load generator.