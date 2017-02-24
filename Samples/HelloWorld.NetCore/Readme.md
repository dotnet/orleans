# HelloWorld.NetCore
Orleans HelloWorld sample targeting .NET Core

## Building the Sample
If you are trying to use a version of Orleans that is not yet released, first run `Build.cmd netstandard` from the root directory of the repository.

Note: If you need to reinstall packages (for example, after making changes to the Orleans runtime and rebuilding the packages), just manually delete all Orleans packages from `(rootfolder)/Samples/{specific-sample}/packages/` and re-run `Build.cmd netstandard`. You might sometimes also need to clean up the NuGet cache folder. In order to do that, run `dotnet nuget locals all --clear`.

You can then compile as usual, build solution.

```
dotnet restore
dotnet build
```

## Running the Sample
To start the silo
```
dotnet src\SiloHost\bin\Debug\netcoreapp1.1\SiloHost.dll
```


To start the client
```
dotnet src\OrleansClient\bin\Debug\netcoreapp1.1\OrleansClient.dll
```
