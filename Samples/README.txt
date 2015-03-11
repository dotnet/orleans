HelloWorldNuget uses Nuget packages available at https://www.nuget.org/packages?q=microsoft.orleans.

In order to run the rest of the samples you need first to install the Orleans installer msi, as described here:
https://github.com/dotnet/orleans/wiki/Installation

If you previously used an older version of Orleans, notably the 0.9 version, we recommend first to completely uninstall it.

The installer installs the Orleans SDK in the folder pointed by [ORLEANS-SDK] environment variable and the samples find it there.
You can then build and run the samples.

We recommend running Azure Samples (AzureWebSample, TicTacToe) in elevated mode in Visual studio.