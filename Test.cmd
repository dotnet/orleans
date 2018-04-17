@if not defined _echo @echo off
setlocal

if not defined BuildConfiguration SET BuildConfiguration=Debug

SET CMDHOME=%~dp0
@REM Remove trailing backslash \
set CMDHOME=%CMDHOME:~0,-1%

call Ensure-DotNetSdk.cmd

pushd "%CMDHOME%"
@cd

SET TestResultDir=%CMDHOME%\Binaries\%BuildConfiguration%\TestResults

if not exist %TestResultDir% md %TestResultDir%

SET _Directory=bin\%BuildConfiguration%\net461\win10-x64

set TESTS=^
%CMDHOME%\test\TesterAzureUtils,^
%CMDHOME%\test\TesterInternal,^
%CMDHOME%\test\Tester,^
%CMDHOME%\test\DefaultCluster.Tests,^
%CMDHOME%\test\NonSilo.Tests,^
%CMDHOME%\test\AWSUtils.Tests,^
%CMDHOME%\test\BondUtils.Tests,^
%CMDHOME%\test\Consul.Tests,^
%CMDHOME%\test\GoogleUtils.Tests,^
%CMDHOME%\test\ServiceBus.Tests,^
%CMDHOME%\test\TestServiceFabric,^
%CMDHOME%\test\TesterAdoNet,^
%CMDHOME%\test\TesterZooKeeperUtils,^
%CMDHOME%\test\RuntimeCodeGen.Tests,^
%CMDHOME%\test\Orleans.Transactions.Tests,^
%CMDHOME%\test\Orleans.Transactions.Azure.Test,^
%CMDHOME%\test\Orleans.Transactions.DynamoDB.Test,^
%CMDHOME%\test\Orleans.TestingHost.Tests,^
%CMDHOME%\test\DependencyInjection.Tests

if []==[%TEST_FILTERS%] set TEST_FILTERS=-trait Category=BVT -trait Category=SlowBVT

@Echo Test assemblies = %TESTS%
@Echo Test filters = %TEST_FILTERS%

PowerShell -NoProfile -ExecutionPolicy Bypass -Command "& ./Parallel-Tests.ps1 -directories %TESTS% -testFilter \"%TEST_FILTERS%\" -outDir '%TestResultDir%' -dotnet '%_dotnet%'"
set testresult=%errorlevel%
popd
endlocal&set testresult=%testresult%
exit /B %testresult%
