@setlocal
@set ECHO=%ECHO%
REM SRC is the folder containing Orleans SDK drop that needs to be copied into Halo tree.
REM DST is the root folder for your local copy of the Halo tree.

@REM set SRC=E:\Depot\Orleans\Code\Binaries\SDK-DROP
@REM set DST=E:\Section-3\Services\Main
set SRC=C:\CCF\Orleans\Code\Binaries\SDK-DROP
set DST=C:\Depot\343 Section 3\Services\Main

set BLDVER=20120720.1

ECHO ""
ECHO ""
ECHO ------------ COPING ORLEANS DLL FROM %SRC% to %DST% ------------


REM Delete all, just to make sure.

attrib -r "%DST%\Nuget.Packages\Microsoft.Research.Orleans.Host.%BLDVER%\lib\net40" /s /d
attrib -r "%DST%\Nuget.Packages\Microsoft.Research.Orleans.Client.%BLDVER%\lib\net40" /s /d
attrib -r "%DST%\External\Orleans\%BLDVER%" /s /d

del /q /s "%DST%\Nuget.Packages\Microsoft.Research.Orleans.Client.%BLDVER%\lib\net40"
del /q /s "%DST%\Nuget.Packages\Microsoft.Research.Orleans.Host.%BLDVER%\lib\net40"
del /q /s "%DST%\External\Orleans\%BLDVER%"
rmdir /q /s "%DST%\External\Orleans\%BLDVER%\Binaries"
rmdir /q /s "%DST%\External\Orleans\%BLDVER%\Docs"
rmdir /q /s "%DST%\External\Orleans\%BLDVER%\LocalSilo"
rmdir /q /s "%DST%\External\Orleans\%BLDVER%\RemoteDeployment"
rmdir /q /s "%DST%\External\Orleans\%BLDVER%\Samples"
rmdir /q /s "%DST%\External\Orleans\%BLDVER%\VisualStudioTemplates"


xcopy /y  %SRC%\Binaries\OrleansClient\Orleans.*  						"%DST%\Nuget.Packages\Microsoft.Research.Orleans.Client.%BLDVER%\lib\net40"
xcopy /y  %SRC%\Binaries\OrleansClient\OrleansRuntimeGrainInterfaces.*  "%DST%\Nuget.Packages\Microsoft.Research.Orleans.Client.%BLDVER%\lib\net40"

xcopy /y  %SRC%\Binaries\OrleansServer\OrleansRuntime.*  			"%DST%\Nuget.Packages\Microsoft.Research.Orleans.Host.%BLDVER%\lib\net40"
xcopy /y  %SRC%\Binaries\OrleansServer\OrleansSiloHost.*  			"%DST%\Nuget.Packages\Microsoft.Research.Orleans.Host.%BLDVER%\lib\net40"
xcopy /y  %SRC%\Binaries\OrleansServer\OrleansAzureUtils.*			"%DST%\Nuget.Packages\Microsoft.Research.Orleans.Host.%BLDVER%\lib\net40"

xcopy /y /E /R %SRC%\*  											"%DST%\External\Orleans\%BLDVER%"


@pause