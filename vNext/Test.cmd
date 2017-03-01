@setlocal
@ECHO off

SET CONFIGURATION=Release

SET CMDHOME=%~dp0
@REM Remove trailing backslash \
set CMDHOME=%CMDHOME:~0,-1%

pushd "%CMDHOME%"
@cd

SET OutDir=%CMDHOME%\Binaries\%CONFIGURATION%

REM set TESTS=%OutDir%\Tester.dll,%OutDir%\TesterInternal.dll,%OutDir%\NonSilo.Tests.dll,%OutDir%\Tester.AzureUtils.dll
set TESTS=%OutDir%\net462\Tester.dll,%OutDir%\net462\NonSilo.Tests.dll,%OutDir%\net462\Tester.AzureUtils.dll,%OutDir%\net462\TesterInternal.dll,%OutDir%\net462\Tester.SQLUtils.dll,%OutDir%\net462\DefaultCluster.Tests.dll,%OutDir%\net462\Consul.Tests.dll,%OutDir%\net462\BondUtils.Tests.dll,%OutDir%\net462\AWSUtils.Tests.dll,%OutDir%\net462\GoogleUtils.Tests.dll,%OutDir%\net462\ServiceBus.Tests.dll
if []==[%TEST_FILTERS%] set TEST_FILTERS=-trait 'Category=BVT' -trait 'Category=SlowBVT'

@Echo Test assemblies = %TESTS%
@Echo Test filters = %TEST_FILTERS%
@echo on
REM call "%CMDHOME%\SetupTestScript.cmd" "%OutDir%"

PowerShell -NoProfile -ExecutionPolicy Bypass -Command "& ./Parallel-Tests.ps1 -assemblies %TESTS% -testFilter \"%TEST_FILTERS%\" -outDir '%OutDir%'"
set testresult=%errorlevel%
popd
endlocal&set testresult=%testresult%
exit /B %testresult%