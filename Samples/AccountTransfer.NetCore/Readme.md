# AccountTransfer.NetCore
Orleans Account Transfer sample targeting .NET Core

## Build and run the sample in Windows
The easiest way to build and run the sample in Windows is to execute the `BuildAndRun.ps1` PowerShell script.

## Build and run the sample in non-Windows platforms
On other platforms you will have to either first build the NuGet packages in a Windows machine (calling `Build.cmd netstandard`) and then make them available in the target platform, 
or use the pre-release packages published in MyGet (see https://dotnet.myget.org/gallery/orleans-prerelease for more information on how to add the feed).
Then just execute the `BuildAndRun.sh` bash script.

## Alternative steps to build and run the sample
Alternatively, you can use the following steps, to understand what is going on:

#### Building the sample
If you are trying to use a version of Orleans that is not yet released, first run `Build.cmd netstandard` from the root directory of the repository.

Note: If you need to reinstall packages (for example, after making changes to the Orleans runtime and rebuilding the packages), just manually delete all Orleans packages from `(rootfolder)/Samples/{specific-sample}/packages/` and re-run `Build.cmd netstandard`. You might sometimes also need to clean up the NuGet cache folder. In order to do that, run `dotnet nuget locals all --clear`.

You can then compile as usual, build solution.

```
dotnet restore
```

#### Running the sample
From Visual Studio, you can start start the SiloHost and OrleansClient projects simultaneously (you can set up multiple startup projects by right-clicking the solution in the Solution Explorer, and select `Set StartUp projects`.

Alternatively, you can run from the command line:

To start the silo
```
dotnet run --project src\SiloHost
```


To start the client (you will have to use a different command window)
```
dotnet run --project src\OrleansClient\
```
