---
layout: page
title: Installation
---
{% include JB/setup %}

The Orleans SDK is available as a single .msi file that installs the Orleans libraries, VSIX extension, and related collateral. You can get the installer from [Releases](https://github.com/dotnet/orleans/releases).

Make sure to read this entire page, as there are some important caveats that may require manual intervention on your part. This is a preview, and the experience has some rough edges, still.

**Note: if you have installed an earlier version of Orleans, you must uninstall it first. Please close Visual Studio while performing the upgrade / installation.**

Starting the installer, you should see the following welcome page:

![](http://download-codeplex.sec.s-msft.com/Download?ProjectName=orleans&DownloadId=816159)

You get a chance to select the destination folder for the installation, but otherwise there are not a lot of installation options. Unless you start the installer with Administrator privileges, you will see a UAC dialog pop up, asking your permission to install.

The recommended (and default) installation location for the Orleans SDK is "C:\Microsoft Project Orleans SDK v1.0" We will refer to the actual directory where the SDK contents are located as [ORLEANS-SDK] throughout the rest of the document.

### Running the Local Orleans Development Silo 
The Orleans SDK includes a local development silo which is pre-configured to run on localhost.

This local silo will allow initial development and testing of Orleans samples and applications, and can also be used as an initial verification step before deployment of changes into a larger cluster of machines.

The Orleans local development silo can be started in a console window using the command script:

[ORLEANS-SDK]\StartLocalSilo.cmd

Once started, the silo can be stopped using Ctrl-C or closing the console window.

For the current release, an Orleans silo must be stopped and restarted in order to pick up any application changes (no dynamic redeployment of code).

### Trouble Building Samples?
Visual Studio does not always seem to immediately pick up environment variables after an installation, and the Orleans SDK depends on one when building Orleans-based projects in VS. (By the way, it's probably something I didn't know how to do in the installer, I'm not saying it's Visual Studio's fault.)

If you have problems loading or building samples after installing the SDK and restarting Visual Studio, you may be running into this issue. For the author, it has manifested itself as failures to load a solution in VS, as well as issues with finding references to the Orleans assemblies within projects that did load.
For example, you might see this:

![](http://download-codeplex.sec.s-msft.com/Download?ProjectName=orleans&DownloadId=819322)

or this:

![](http://download-codeplex.sec.s-msft.com/Download?ProjectName=orleans&DownloadId=817451)

and get the following error message in the Output window:

    C:\dd\Orleans\git\orleans\src\samples\HelloWorld\HelloWorldInterfaces\HelloWorldInterfaces.csproj : error  : The imported project "C:\Binaries\OrleansClient\Orleans.SDK.targets" was not found. Confirm that the path in the <Import> declaration is correct, and that the file exists on disk.  C:\dd\Orleans\git\orleans\src\samples\HelloWorld\HelloWorldInterfaces\HelloWorldInterfaces.csproj
    
    C:\dd\Orleans\git\orleans\src\samples\HelloWorld\HelloWordGrains\HelloWordGrains.csproj : error  : The imported project "C:\Binaries\OrleansClient\Orleans.SDK.targets" was not found. Confirm that the path in the <Import> declaration is correct, and that the file exists on disk.  C:\dd\Orleans\git\orleans\src\samples\HelloWorld\HelloWordGrains\HelloWordGrains.csproj

Note, in particular, that the path of the included project starts with "C:\Binaries" -- that means the path in between, defined by the environment variable 'OrleansSDK,' is not defined.

If this is your situation, you should try these steps to work around it before doing any other troubleshooting.

Close all sessions of Visual Studio that you have open.

Open the Control Panel and go to the 'System' section.

![](http://download-codeplex.sec.s-msft.com/Download?ProjectName=orleans&DownloadId=816161)

Choose the 'Advanced system settings' link

![](http://download-codeplex.sec.s-msft.com/Download?ProjectName=orleans&DownloadId=816162)

then, click the 'Environment Variables' button and find the 'OrleansSDK' variable among the system variables:

![](http://download-codeplex.sec.s-msft.com/Download?ProjectName=orleans&DownloadId=816163)

Click the 'Edit' button, but don't actually edit anything.

![](http://download-codeplex.sec.s-msft.com/Download?ProjectName=orleans&DownloadId=816164)

 Click 'OK' all the way out and close the Control Panel.

 Restart Visual Studio and try loading the solution again.
