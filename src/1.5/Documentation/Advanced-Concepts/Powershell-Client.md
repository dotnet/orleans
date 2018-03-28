---
layout: page
title: PowerShell Client Module
---

# PowerShell Client Module

The Orleans PowerShell Client Module is a set of [PowerShell Cmdlets](https://technet.microsoft.com/en-us/library/dd772285.aspx) that wraps
[GrainClient](https://github.com/dotnet/orleans/blob/master/src/Orleans/Core/GrainClient.cs) in a set of convenient commands making possible to interact with not just
[ManagementGrain](https://github.com/dotnet/orleans/blob/master/src/OrleansRuntime/Core/ManagementGrain.cs) but any `IGrain` just as a regular Orleans application can by using Powershell scripts.

These Cmdlets enable a series of scenarios from start maintenance tasks, tests, monitoring or any other kind of automation by leveraging Powershell scripts.

Here is how to use it:

## Installing the module

### From Source
You can build from source the `OrleansPSUtils` project and just import it with:

``` powershell
PS> Import-Module .\projectOutputDir\Orleans.psd1

```

Althought you can do that, there is a much easier and interesting way for doing that by installing it from **PowerShell Gallery**.

### From PowerShell Gallery

Powershell modules today are easily shared just as Nuget packages but instead of nuget.org, they are hosted on [PowerShell Gallery](https://www.powershellgallery.com/).

* To install it on a specific folder just run:

``` powershell
PS> Save-Module -Name OrleansPSUtils -Path <path>

```

* To install it on your PowerShell modules path (**the recommended way**), just run:

``` powershell
PS> Install-Module -Name OrleansPSUtils

```

* If you plan to use this module on an [Azure Automation](https://azure.microsoft.com/en-us/services/automation/), just click on the button bellow:
<button style="border:none;background-image:none; background-color:transparent " type="button" title="Deploy this module to Azure Automation." onclick="window.open('https://www.powershellgallery.com/packages/Orleans/DeployItemToAzureAutomation?itemType=PSModule', target = '_blank')">
	<img src="https://www.powershellgallery.com/Content/Images/DeployToAzureAutomationButton.png">
</button>

## Using the module

Regardless of the way you decide to install it, the first thing you need to do in order to actually use it is import the module on the current PowerShell session so the Cmdlets get available by running this:

``` powershell
PS> Import-Module OrleansPSUtils
```

**Note**:
In case of building from source, you must import it as suggested on the Install section by using the path to the `.psd1` instead of using the module name since it will not be on the `$env:PSModulePath` PowerShell runtime variable.
Again, it is highly recommended that you install from PowerShell Gallery instead.

After the module is imported (which means it is loaded on PowerShell session), you will have the following Cmdlets available:

* `Start-GrainClient`
* `Stop-GrainClient`
* `Get-Grain`

#### Start-GrainClient

This module is a wrapper around `GrainClient.Initialize()` and its overloads.

**Usage**:     

* __`Start-GrainClient`__

  * The same as call `GrainClient.Initialize()` which will look for the known Orleans Client configuration file names

* __`Start-GrainClient [-ConfigFilePath] <string> [[-Timeout] <timespan>]`__

  * Will use the provided file path as in `GrainClient.Initialize(filePath)`

* __`Start-GrainClient [-ConfigFile] <FileInfo> [[-Timeout] <timespan>]`__

  * Use an instance of the `System.FileInfo` class representing the config file just as `GrainClient.Initialize(fileInfo)`

* __`Start-GrainClient [-Config] <ClientConfiguration> [[-Timeout] <timespan>]`__

  * Use an instance of a `Orleans.Runtime.Configuration.ClientConfiguration` like in `GrainClient.Initialize(config)`

* __`Start-GrainClient [-GatewayAddress] <IPEndPoint> [[-OverrideConfig] <bool>] [[-Timeout] <timespan>]`__

  * Takes a Orleans Cluster Gateway Address Endpoint


**Note**:
The `Timeout` parameter is optional and if it is informed and greater than `System.TimeSpan.Zero`, it will call `Orleans.GrainClient.SetResponseTimeout(Timeout)` internally.

#### Stop-GrainClient

Takes no parameters and when called, if the `GrainClient` is initialized will gracefuly uninitialize.

#### Get-Grain

Wrapper around `GrainClient.GrainFactory.GetGrain<T>()` and its overloads.

The mandatory parameter is `-GrainType` and the `-XXXKey` for the current Grain key types supported by Orleans (`string`, `Guid`, `long`) and also the `-KeyExtension` that can be used on Grains with compound keys.

This Cmdlet return a grain reference of the type passed by as parameter on `-GrainType`.

## Example:

A simple example on calling `MyInterfacesNamespace.IMyGrain.SayHeloTo` grain method:

``` powershell
PS> Import-Module OrleansPSUtils
PS> $configFilePath = Resolve-Path(".\ClientConfig.xml").Path
PS> Start-GrainClient -ConfigFilePath $configFilePath
PS> Add-Type -Path .\MyGrainInterfaceAssembly.dll
PS> $grainInterfaceType = [MyInterfacesNamespace.IMyGrain]
PS> $grainId = [System.Guid]::Parse("A4CF7B5D-9606-446D-ACE9-C900AC6BA3AD")
PS> $grain = Get-Grain -GrainType $grainInterfaceType -GuidKey $grainId
PS> $message = $grain.SayHelloTo("Gutemberg").Result
PS> Write-Output $message
Hello Gutemberg!
PS> Stop-GrainClient
```

We plan to update this page as we introduce more Cmdlets like use Observers, Streams and other Orleans core features more natively on Powershell.
We hope that this help people as a starting point for automation. As always, this is a work-in-progress and we love contributions! :)

Please note that the intent is not to reimplement the whole client on PowerShell but instead, give IT and DevOps teams a way to interact with the Grains without need to implement a .Net application.
