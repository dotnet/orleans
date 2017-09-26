@if not defined _echo @echo off
setlocal

if not defined BuildConfiguration SET BuildConfiguration=Debug

SET CMDHOME=%~dp0
@REM Remove trailing backslash \
set CMDHOME=%CMDHOME:~0,-1%

pushd "%CMDHOME%"
@cd

SET TestResultDir=%CMDHOME%\Binaries\%BuildConfiguration%\TestResults

if not exist %TestResultDir% md %TestResultDir%

SET _Directory=bin\%BuildConfiguration%\net461\win10-x64

rem copy Versioning dlls to the appropriate place to make Versioning tests pass.
if not exist %CMDHOME%\test\Tester\%_Directory%\TestVersionGrainsV1\ mkdir %CMDHOME%\test\Tester\%_Directory%\TestVersionGrainsV1
if not exist %CMDHOME%\test\Tester\%_Directory%\TestVersionGrainsV2\ mkdir %CMDHOME%\test\Tester\%_Directory%\TestVersionGrainsV2

copy %CMDHOME%\test\Versions\TestVersionGrains\%_Directory%\* %CMDHOME%\test\Tester\%_Directory%\TestVersionGrainsV1\
copy %CMDHOME%\test\Versions\TestVersionGrains2\%_Directory%\* %CMDHOME%\test\Tester\%_Directory%\TestVersionGrainsV2\

set TESTS=%CMDHOME%\test\AWSUtils.Tests\%_Directory%\AWSUtils.Tests.dll,%CMDHOME%\test\BondUtils.Tests\%_Directory%\BondUtils.Tests.dll,%CMDHOME%\test\Consul.Tests\%_Directory%\Consul.Tests.dll,%CMDHOME%\test\DefaultCluster.Tests\%_Directory%\DefaultCluster.Tests.dll,%CMDHOME%\test\GoogleUtils.Tests\%_Directory%\GoogleUtils.Tests.dll,%CMDHOME%\test\NonSilo.Tests\%_Directory%\NonSilo.Tests.dll,%CMDHOME%\test\ServiceBus.Tests\%_Directory%\ServiceBus.Tests.dll,%CMDHOME%\test\TestServiceFabric\%_Directory%\TestServiceFabric.dll,%CMDHOME%\test\Tester\%_Directory%\Tester.dll,%CMDHOME%\test\TesterAzureUtils\%_Directory%\Tester.AzureUtils.dll,%CMDHOME%\test\TesterInternal\%_Directory%\TesterInternal.dll,%CMDHOME%\test\TesterSQLUtils\%_Directory%\Tester.SQLUtils.dll,%CMDHOME%\test\TesterZooKeeperUtils\%_Directory%\Tester.ZooKeeperUtils.dll

if []==[%TEST_FILTERS%] set TEST_FILTERS=-trait 'Category=BVT' -trait 'Category=SlowBVT'

@Echo Test assemblies = %TESTS%
@Echo Test filters = %TEST_FILTERS%

PowerShell -NoProfile -ExecutionPolicy Bypass -Command "& ./Parallel-Tests.ps1 -assemblies %TESTS% -testFilter \"%TEST_FILTERS%\" -outDir '%TestResultDir%'"
set testresult=%errorlevel%
popd
endlocal&set testresult=%testresult%
exit /B %testresult%