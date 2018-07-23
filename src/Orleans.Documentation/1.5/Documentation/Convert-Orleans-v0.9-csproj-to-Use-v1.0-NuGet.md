---
layout: page
title: Convert Orleans v0.9 csproj to use V1.0 NuGet
---

[!include[](../warning-banner.md)]

# How To Convert a .csproj File To Use Orleans NuGet Packages

This note shows how to manually convert a Visual Studio .csproj file which was created with Orleans v0.9 Visual Studio templates from using assembly references based on the $(OrleansSDK) environment variable to using Orleans NuGet packages.

These examples assume you are using the latest v1.0.3 NuGet packages from Feb-2015.

For simplicity I will use "v0.9" to refer to the "old" .csproj and "v1.0" to refer to the "new" .csproj although strictly speaking "v1.0 really means ">= v1.0.3" in practice because there was significant restructuring and changes in the Orleans NuGet packages before that point.

# Grain Interface Project

## Orleans Grain Interface project -- Example: HelloWorldInterfaces.csproj

_You might want to preserve a copy of the old .csproj file before you start, if you do not have a copy already preserved in your source code control system._

Steps to to change Orleans Grain Interface project:

1. Do Build->Clean on the project to remove any old binaries.
2. Remove old v0.9 assembly references for any Orleans binaries.

	``` xml
	<ItemGroup>
	    <Reference Include="Orleans">
	          <HintPath>$(OrleansSDK)\Binaries\OrleansClient\Orleans.dll</HintPath>
	          <Private>False</Private>
	     </Reference>
	</ItemGroup>
	```

3. Remove old v0.9 Orleans code-gen metadata and script trigger.

	``` xml
	<PropertyGroup>
	      <OrleansProjectType>Server</OrleansProjectType>
	</PropertyGroup>
	<Import Project="$(OrleansSDK)\Binaries\OrleansClient\Orleans.SDK.targets" />
	```
	
4. **Make sure to re-save the .csproj to disk at this point, otherwise the next step will fail !**
5. Use Visual Studio Package manager to add the `Microsoft.Orleans.Templates.Interfaces` package to the grain interfaces project.
  * Do this by right-click on project node in Solution Explorer, select "Manage NuGet Packages..." context menu item, then search for **Orleans** and select the `Microsoft.Orleans.Templates.Interfaces` package.
  * This will add a `packages.config` file to the project, and add the normal NuGet link code into the .csproj
  * This will also add the Orleans assembly references into the project, and recreate the code-gen metadata and script links for you.
6. Ensure .csproj file is saved to disk again.
7. Do Build->Rebuild on the project to rebuild with the new packages and binaries.
8. Going forward, you should only need to change the version number in packages.config to use a newer package -- either manually edit the `packages.config` file or use NuGet Package Manager UI in Visual Studio.

## Orleans Grain Class project -- Example: HelloWorldGrains.csproj

Steps to to change Orleans Grain Class project:

Steps 1..4 and 6..8 are the same as for the grain interfaces .csproj above.

1. Build->Clean
2. Remove old v0.9 Orleans assembly references
3. Remove old v0.9 Orleans code-gen metadata
4. **Save .csproj file to disk**
5. Use Visual Studio Package manager to add the `Microsoft.Orleans.Templates.Grains` package to the  project.
6. Follow Steps 6..8 from the grain interfaces .csproj above.

## Problems?

If you have problems getting these conversions to work for you, then please post a **full** copy of your .csproj into a [Gist](https://gist.github.com/) on your GitHub account and then open a new Issue in the [Orleans GitHub Project](https://github.com/dotnet/orleans/issues) asking for assistance and pointing to the Gist for your specific project file.
