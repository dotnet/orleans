@if not defined _echo @echo off
setlocal

if not defined BuildConfiguration SET BuildConfiguration=Debug

SET CMDHOME=%~dp0
@REM Remove trailing backslash \
set CMDHOME=%CMDHOME:~0,-1%

:: Disable multilevel lookup https://github.com/dotnet/core-setup/blob/master/Documentation/design-docs/multilevel-sharedfx-lookup.md
set DOTNET_MULTILEVEL_LOOKUP=0 

call Ensure-DotNetSdk.cmd

pushd "%CMDHOME%"
@cd

SET TestResultDir=%CMDHOME%\Binaries\%BuildConfiguration%\TestResults

if not exist %TestResultDir% md %TestResultDir%

SET _Directory=bin\%BuildConfiguration%\net461\win10-x64

set TESTS=^
%CMDHOME%\test\Extensions\TesterAzureUtils,^
%CMDHOME%\test\TesterInternal,^
%CMDHOME%\test\Tester,^
%CMDHOME%\test\DefaultCluster.Tests,^
%CMDHOME%\test\NonSilo.Tests,^
%CMDHOME%\test\Extensions\AWSUtils.Tests,^
%CMDHOME%\test\Extensions\Serializers\BondUtils.Tests,^
%CMDHOME%\test\Extensions\Consul.Tests,^
%CMDHOME%\test\Extensions\Serializers\GoogleUtils.Tests,^
%CMDHOME%\test\Extensions\ServiceBus.Tests,^
%CMDHOME%\test\Extensions\TestServiceFabric,^
%CMDHOME%\test\Extensions\TesterAdoNet,^
%CMDHOME%\test\Extensions\TesterZooKeeperUtils,^
%CMDHOME%\test\RuntimeCodeGen.Tests,^
%CMDHOME%\test\Transactions\Orleans.Transactions.Tests,^
%CMDHOME%\test\Transactions\Orleans.Transactions.Azure.Test,^
%CMDHOME%\test\TestInfrastructure\Orleans.TestingHost.Tests,^
%CMDHOME%\test\DependencyInjection.Tests

:: Add to TESTS once dotnet-xunit supports .NET Core 3.0 (post dotnet-xunit v2.4.1)
rem %CMDHOME%\test\Orleans.Connections.Security.Tests
rem %CMDHOME%\test\NetCore.Tests

rem %CMDHOME%\test\Analyzers.Tests

if []==[%TEST_FILTERS%] set "TEST_FILTERS=Category=BVT^|Category=SlowBVT"

@Echo Test assemblies = %TESTS%
@Echo Test filters = %TEST_FILTERS%

PowerShell -NoProfile -ExecutionPolicy Bypass -Command "& ./Parallel-Tests.ps1 -directories %TESTS% -testFilter '%TEST_FILTERS%' -outDir '%TestResultDir%' -dotnet '%_dotnet%'"
set testresult=%errorlevel%
popd
endlocal&set testresult=%testresult%
exit /B %testresult%
