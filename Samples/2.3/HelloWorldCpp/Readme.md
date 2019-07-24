# HelloWorld C++
Orleans HelloWorld sample demonstrating how to use a C++ client wrapper.

The idea on this Sample is to show how embed .Net Core CLR in a C++ console application and invoke grain methods from the C++ code.

> **Disclaimer**: The C++ code is **not** production code. It is just for illustration purpose. You can do it on your production application but please create a proper C++ embedder.

The C++ code is based on .Net Team's sample to host the .Net Core CLR. It was just modified for this Sample so it can easily host Orleans.

## Build and run the sample in Windows
The easiest way to build and run the sample in Windows is to execute the `BuildAndRun.ps1` PowerShell script.

> .Net Core runtime is a 64bit binary. Since this is a native application, in order to build it correctly on Windows, run `BuildAndRun.ps1` from `Visual Studio Native x64 Command Prompt`.

## Build and run the sample in non-Windows platforms
On other platforms you will have to either first build the NuGet packages in a Windows machine (calling `Build.cmd netstandard`) and then make them available in the target platform, 
or use the pre-release packages published in MyGet (see https://dotnet.myget.org/gallery/orleans-prerelease for more information on how to add the feed).
Then just execute the `BuildAndRun.sh` bash script for Linux and `BuildAndRunOSX.sh` for Mac OSX.

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

- VSCode: Open the `HelloWorld.code-workspace` on VSCode and run it.
- Visual Studio 2017/2019: Install the C++ workload from Visual Studio 2017/2019 Installer. Open the `HelloWorld.sln` and run both `SiloHost` and `OrleansClientCpp` projects.

