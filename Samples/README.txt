HelloWorldNuget uses Orleans Nuget packages available at https://www.nuget.org/packages?q=microsoft.orleans.

In order to run the rest of the samples you need first to install the Orleans installer msi, as described here: http://dotnet.github.io/orleans/Installation

Samples are always runnable against the latest officially published SDK located here: https://github.com/dotnet/orleans/releases/latest

If you previously used an older version of Orleans, notably the 0.9 version, we recommend first to completely uninstall it.

The installer installs the Orleans SDK in the folder pointed by [ORLEANS-SDK] environment variable and the samples find it there.
You can then build and run the samples.

We recommend running Azure Samples (AzureWebSample, TicTacToe) in elevated mode in Visual studio.

All other non-Orleans dependencies, such as Azure Storage, Azure Service Runtime, etc... are consumed via Nuget packages.
