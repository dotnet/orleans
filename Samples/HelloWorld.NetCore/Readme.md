# HelloWorld.NetCore
Orleans HelloWorld sample targeting .NET Core

If you are trying to use a version of Orleans that is not yet released, first run `Build.cmd netstandard` from the root directory of the repository.

Note: If you need to reinstall packages (for example, after making changes to the Orleans runtime and rebuilding the packages), just manually delete all Orleans packages from `(rootfolder)/Samples/{specific-sample}/packages/` and re-run `Build.cmd netstandard`. You might sometimes also need to clean up the NuGet cache folder.

You can then compile as usual, build solution.
