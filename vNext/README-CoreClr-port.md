This folder contains the working place for the migration effort to .NET Standard and .NET Core (https://github.com/dotnet/orleans/issues/2143).
This is very much in progress and we welcome help.

The `vNext` tree has a separate solution (`src/Orleans.vNext.sln`) with several projects that have links to the code files in the main (non-vNext) solution.
Currently not all of the original projects were migrated to target .NET Standard, and we'll add them as we need and can (for example, we first need to get a few basic test projects up so that we can start asserting the port is working as expected).

We mainly use 2 different compilation constant to differentiate between the 2 solutions (via conditional compilation):
- NETSTANDARD: When something is just different between the 2 targets. Long term we want to avoid multi-targeting library projects for full .NET Framework and .NET Standard. So it means that this compilation constant will eventually be dropped, but we do not expect this soon (at least not before the WALK phase defined in issue #2143).
- NETSTANDARD_TODO: short term temporary conditional compilation to get the project to build, but requires fixing before that feature is functional. This was mainly added to get the project bootstrapped and compiling so that the community can start contributing these pieces that were temporarily left out.

A good start if you want to contribute is to look at the code left out with `#if !NETSTANDARD_TODO` and submit pull requests for them. Ideally the code should not be a breaking change for people using Orleans today, and if there is need to do a breaking change, we can discuss it in an issue.